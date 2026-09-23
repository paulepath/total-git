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

/// <summary>A file or folder in the worktree is held open by another program and couldn't be deleted.</summary>
public sealed class WorktreeLockedException(string path, string lockedPath)
    : Exception($"Couldn't delete '{lockedPath}' because it is in use by another program.")
{
    public string Path { get; } = path;
    public string LockedPath { get; } = lockedPath;
}

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
    /// Removes a worktree, or a leftover folder under <c>.worktrees</c> that git no longer tracks.
    /// Links created by <see cref="LinkNodeModules"/> are unlinked first so nothing is ever deleted
    /// through them. Throws <see cref="WorktreeDirtyException"/> when the worktree has changes and
    /// <paramref name="force"/> is false, and <see cref="WorktreeLockedException"/> when a file stays
    /// in use (Windows) after retrying; calling again once it is released finishes the removal.
    /// </summary>
    public static async Task RemoveAsync(string mainRoot, string worktreePath, bool force = false, TimeSpan? retryDelay = null)
    {
        var delay = retryDelay ?? TimeSpan.FromMilliseconds(400);
        var registered = await IsRegisteredAsync(mainRoot, worktreePath);

        if (Directory.Exists(worktreePath))
        {
            if (registered && !force) await EnsureCleanAsync(worktreePath, delay);
            RemoveLinks(worktreePath);
        }

        if (registered)
        {
            // Git stops at the first file it can't delete; on Windows that is usually a short-lived
            // lock (editor, indexer, antivirus), so retry before deleting the rest ourselves.
            string[] args = ["worktree", "remove", "--force", worktreePath];
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = await GitCli.RunAsync(mainRoot, args, throwOnError: false);
                if (result.ExitCode == 0) return;
                if (!Directory.Exists(worktreePath) || !IsDeleteFailure(result.StdErr))
                    throw new GitCommandException(string.Join(' ', args), result);
                await Task.Delay(delay);
                if (!await IsRegisteredAsync(mainRoot, worktreePath)) break;
            }
        }

        if (Directory.Exists(worktreePath)) await DeleteDirectoryAsync(worktreePath, delay);
        await PruneAsync(mainRoot);
    }

    /// <summary>
    /// Throws <see cref="WorktreeDirtyException"/> if the worktree has changes. Git can't read a
    /// locked file, so it reports it as modified; such files are retried and then reported as
    /// <see cref="WorktreeLockedException"/> rather than as uncommitted work.
    /// </summary>
    private static async Task EnsureCleanAsync(string worktreePath, TimeSpan delay)
    {
        string? locked = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var status = await GitCli.RunAsync(worktreePath, "status", "--porcelain");
            // Porcelain lines are "XY path" where X or Y may be a space, so don't trim them.
            var changes = status.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 3)
                .ToArray();
            if (changes.Length == 0) return;

            locked = changes
                .Select(line => Path.Combine(worktreePath, line[3..].Split(" -> ")[^1].Trim('"')))
                .FirstOrDefault(IsLocked);
            if (locked is null) throw new WorktreeDirtyException(worktreePath, changes);
            await Task.Delay(delay);
        }
        throw new WorktreeLockedException(worktreePath, locked!);
    }

    private static bool IsLocked(string file)
    {
        if (!File.Exists(file)) return false;
        try
        {
            using var _ = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsDeleteFailure(string stderr) =>
        stderr.Contains("failed to delete", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Directory not empty", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> IsRegisteredAsync(string mainRoot, string worktreePath) =>
        (await WorktreeService.ListAsync(mainRoot)).Any(w => !w.IsMain && WorktreeService.SamePath(w.Path, worktreePath));

    /// <summary>
    /// Deletes a folder tree, clearing read-only flags, removing links without following them, and
    /// retrying items that are briefly in use. Throws <see cref="WorktreeLockedException"/> naming
    /// the first item still locked after the last attempt.
    /// </summary>
    public static async Task DeleteDirectoryAsync(string root, TimeSpan retryDelay, int attempts = 5)
    {
        string? locked = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            locked = TryDeleteTree(root);
            if (locked is null) return;
            await Task.Delay(retryDelay);
        }
        throw new WorktreeLockedException(root, locked!);
    }

    /// <summary>Deletes as much of the tree as possible; returns the first path that couldn't be deleted.</summary>
    private static string? TryDeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        string? firstFailure = null;

        foreach (var sub in SafeDirectories(dir))
        {
            var failed = IsLink(sub) ? TryDelete(() => Directory.Delete(sub), sub) : TryDeleteTree(sub);
            firstFailure ??= failed;
        }
        foreach (var file in SafeFiles(dir))
        {
            var failed = TryDelete(() =>
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                File.Delete(file);
            }, file);
            firstFailure ??= failed;
        }
        return firstFailure ?? TryDelete(() => Directory.Delete(dir), dir);
    }

    private static string? TryDelete(Action delete, string path)
    {
        try
        {
            delete();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
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
