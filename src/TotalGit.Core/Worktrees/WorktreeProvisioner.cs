using System.Diagnostics;
using System.IO.Enumeration;
using TotalGit.Core.Git;

namespace TotalGit.Core.Worktrees;

public enum WorktreeSource
{
    /// <summary>Check out an existing local branch.</summary>
    LocalBranch,

    /// <summary>Create a local branch tracking a remote branch.</summary>
    RemoteBranch,

    /// <summary>Create a new branch from a start point (commit or branch).</summary>
    NewBranch,
}

/// <param name="MainRoot">Root of the main worktree; the new worktree goes in <c>MainRoot/.worktrees/Name</c>.</param>
/// <param name="Branch">Local branch to check out or create.</param>
/// <param name="StartPoint">For <see cref="WorktreeSource.RemoteBranch"/> the remote branch (<c>origin/x</c>); for <see cref="WorktreeSource.NewBranch"/> the commit or branch to start from.</param>
public sealed record WorktreeCreateRequest(
    string MainRoot,
    string Name,
    WorktreeSource Source,
    string Branch,
    string? StartPoint = null,
    bool CopyLocalFiles = true,
    IReadOnlyList<string>? LocalFilePatterns = null,
    bool LinkNodeModules = true);

public sealed record WorktreeCreateResult(
    string Path,
    string Branch,
    bool GitIgnoreUpdated,
    IReadOnlyList<string> CopiedFiles,
    IReadOnlyList<string> SkippedFiles,
    IReadOnlyList<string> LinkedFolders);

public sealed class WorktreeDirtyException(string path, IReadOnlyList<string> changes)
    : Exception($"The worktree at '{path}' has uncommitted changes.")
{
    public string Path { get; } = path;
    public IReadOnlyList<string> Changes { get; } = changes;
}

/// <summary>
/// Creates and removes worktrees under <c>.worktrees/</c>, copying gitignored local files and
/// linking <c>node_modules</c> so the new worktree is ready to run.
/// </summary>
public static class WorktreeProvisioner
{
    public static readonly IReadOnlyList<string> DefaultLocalFilePatterns =
        [".env", "appsettings.Local.json", "appsettings.user.json", "local.settings.json"];

    /// <summary>Folders never searched when copying local files or linking node_modules.</summary>
    public static readonly IReadOnlyList<string> ExcludedFolders = ["bin", "obj", "node_modules", ".git", WorktreeService.WorktreesFolder];

    public static async Task<WorktreeCreateResult> CreateAsync(WorktreeCreateRequest request)
    {
        var path = WorktreeService.PathFor(request.MainRoot, request.Name);
        if (Directory.Exists(path) || File.Exists(path))
            throw new InvalidOperationException($"'{path}' already exists.");

        var gitIgnoreUpdated = EnsureGitIgnore(request.MainRoot);

        string[] args = request.Source switch
        {
            WorktreeSource.LocalBranch => ["worktree", "add", path, request.Branch],
            WorktreeSource.RemoteBranch => ["worktree", "add", "--track", "-b", request.Branch, path,
                request.StartPoint ?? throw new ArgumentException("A remote branch is required.", nameof(request))],
            WorktreeSource.NewBranch => ["worktree", "add", "-b", request.Branch, path, request.StartPoint ?? "HEAD"],
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        await GitCli.RunAsync(request.MainRoot, args);

        var copied = new List<string>();
        var skipped = new List<string>();
        if (request.CopyLocalFiles)
            CopyLocalFiles(request.MainRoot, path, request.LocalFilePatterns ?? DefaultLocalFilePatterns, copied, skipped);

        var linked = request.LinkNodeModules ? LinkNodeModules(request.MainRoot, path) : [];

        return new WorktreeCreateResult(path, request.Branch, gitIgnoreUpdated, copied, skipped, linked);
    }

    /// <summary>
    /// Removes a worktree. Links created by <see cref="LinkNodeModules"/> are unlinked first so
    /// nothing is ever deleted through them. Throws <see cref="WorktreeDirtyException"/> when the
    /// worktree has changes and <paramref name="force"/> is false.
    /// </summary>
    public static async Task RemoveAsync(string mainRoot, string worktreePath, bool force = false)
    {
        if (Directory.Exists(worktreePath))
        {
            if (!force)
            {
                var status = await GitCli.RunAsync(worktreePath, "status", "--porcelain");
                var changes = status.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (changes.Length > 0) throw new WorktreeDirtyException(worktreePath, changes);
            }
            RemoveLinks(worktreePath);
        }

        string[] args = force ? ["worktree", "remove", "--force", worktreePath] : ["worktree", "remove", worktreePath];
        await GitCli.RunAsync(mainRoot, args);
    }

    public static Task PruneAsync(string mainRoot) => GitCli.RunAsync(mainRoot, "worktree", "prune");

    /// <summary>Adds <c>.worktrees/</c> to the root .gitignore if not already ignored there. Returns true if changed.</summary>
    public static bool EnsureGitIgnore(string mainRoot)
    {
        var file = Path.Combine(mainRoot, ".gitignore");
        var existing = File.Exists(file) ? File.ReadAllText(file) : "";
        var already = existing
            .Split('\n')
            .Select(l => l.Trim())
            .Any(l => l is ".worktrees" or ".worktrees/" or "/.worktrees" or "/.worktrees/");
        if (already) return false;

        var newline = existing.Contains("\r\n") ? "\r\n" : "\n";
        var prefix = existing.Length == 0 || existing.EndsWith('\n') ? "" : newline;
        File.AppendAllText(file, $"{prefix}{newline}# Local worktrees{newline}.worktrees/{newline}");
        return true;
    }

    /// <summary>
    /// Copies files whose name matches one of <paramref name="patterns"/> from <paramref name="sourceRoot"/>
    /// into the same relative location under <paramref name="destRoot"/>, skipping files already present.
    /// </summary>
    public static void CopyLocalFiles(string sourceRoot, string destRoot, IEnumerable<string> patterns, List<string> copied, List<string> skipped)
    {
        var patternList = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray();
        if (patternList.Length == 0) return;

        foreach (var file in Walk(sourceRoot, ExcludedFolders, collectFolder: null))
        {
            var name = Path.GetFileName(file);
            if (!patternList.Any(p => FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true))) continue;

            var relative = Path.GetRelativePath(sourceRoot, file);
            var dest = Path.Combine(destRoot, relative);
            if (File.Exists(dest))
            {
                skipped.Add(relative);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
            copied.Add(relative);
        }
    }

    /// <summary>
    /// For each outermost <c>node_modules</c> folder in the source whose parent folder exists in the
    /// destination, creates a directory junction (symlink off Windows) pointing back at the source.
    /// </summary>
    public static IReadOnlyList<string> LinkNodeModules(string sourceRoot, string destRoot)
    {
        var folders = new List<string>();
        var excluded = ExcludedFolders.Where(f => f != "node_modules").ToArray();
        foreach (var _ in Walk(sourceRoot, excluded, collectFolder: d =>
                 {
                     if (Path.GetFileName(d) != "node_modules") return false;
                     folders.Add(d);
                     return true; // don't descend into it
                 })) { }

        var linked = new List<string>();
        foreach (var source in folders)
        {
            var relative = Path.GetRelativePath(sourceRoot, source);
            var dest = Path.Combine(destRoot, relative);
            if (!Directory.Exists(Path.GetDirectoryName(dest)) || Directory.Exists(dest) || File.Exists(dest)) continue;

            CreateJunction(dest, source);
            linked.Add(relative);
        }
        return linked;
    }

    /// <summary>
    /// Deletes the <c>node_modules</c> links under <paramref name="root"/> without touching their
    /// targets. Other links are left alone: they may be tracked content.
    /// </summary>
    public static void RemoveLinks(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            foreach (var dir in SafeDirectories(pending.Pop()))
            {
                var name = Path.GetFileName(dir);
                if (IsLink(dir))
                {
                    if (name == "node_modules") Directory.Delete(dir); // non-recursive: removes the link only
                }
                else if (name is not (".git" or "node_modules"))
                {
                    pending.Push(dir);
                }
            }
        }
    }

    public static bool IsLink(string directory) =>
        new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static void CreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        // Junctions need no admin rights or developer mode, unlike directory symlinks.
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in (string[])["/c", "mklink", "/J", link, target]) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var error = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException($"Failed to link '{link}' to '{target}': {error.Trim()}");
    }

    /// <summary>Enumerates files under <paramref name="root"/>, pruning excluded folders and never following links.</summary>
    private static IEnumerable<string> Walk(string root, IReadOnlyList<string> excluded, Func<string, bool>? collectFolder)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in SafeDirectories(dir))
            {
                if (IsLink(sub)) continue;
                if (collectFolder?.Invoke(sub) == true) continue;
                if (excluded.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) continue;
                pending.Push(sub);
            }
            foreach (var file in SafeFiles(dir)) yield return file;
        }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return []; }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return []; }
    }
}
