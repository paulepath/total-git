namespace TotalGit.Core.Git;

/// <summary>A rebase step git couldn't start, and the untracked files in its way.</summary>
/// <param name="UntrackedInTheWay">Files the commit adds that exist here untracked (git won't overwrite them).</param>
public sealed record BlockedPick(string Sha, string Subject, IReadOnlyList<string> UntrackedInTheWay);

/// <summary>Files put back after a rebase, and any that couldn't be (something else is at their path now).</summary>
public sealed record RestoreResult(IReadOnlyList<string> Restored, IReadOnlyList<string> LeftAside, string Folder);

/// <summary>
/// A rebase that stops on the same commit every time: git refuses to apply a commit when it would overwrite
/// untracked files, puts the step back on the to-do list and stops. Continuing just tries again (and the step count
/// goes up each time). That happens when a later commit stopped tracking files that are still on disk (now ignored):
/// replaying the branch from before that point finds them untracked and in the way.
/// </summary>
public static class RebaseBlock
{
    private const string AsideFolder = "totalgit-moved-aside";

    /// <summary>The step a paused rebase keeps failing to start, or null when it isn't stuck that way.</summary>
    public static async Task<BlockedPick?> FindAsync(string worktree)
    {
        var dir = await GitPathAsync(worktree, "rebase-merge");
        var done = LastCommand(Path.Combine(dir, "done"), last: true);
        var todo = LastCommand(Path.Combine(dir, "git-rebase-todo"), last: false);
        // A step that failed before it changed anything is the last one done and, rescheduled, the next one to do.
        if (done is null || todo is null || done != todo) return null;
        var sha = done.Value.Sha;

        var added = await GitCli.RunAsync(worktree, ["diff-tree", "-r", "--root", "--no-commit-id", "--name-only", "-z", "--diff-filter=A", sha],
            throwOnError: false);
        if (added.ExitCode != 0) return null;
        var paths = added.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        var untracked = new List<string>();
        foreach (var batch in paths.Chunk(50))
        {
            var listed = await GitCli.RunAsync(worktree, ["--literal-pathspecs", "ls-files", "--others", "--exclude-standard", "-z", "--", .. batch],
                throwOnError: false);
            if (listed.ExitCode == 0) untracked.AddRange(listed.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }
        var subject = (await GitCli.RunAsync(worktree, ["log", "-1", "--format=%s", sha], throwOnError: false)).StdOut.Trim();
        return new BlockedPick(sha, subject, untracked);
    }

    /// <summary>The command and commit of the first (or last) step in a rebase to-do file.</summary>
    private static (string Command, string Sha)? LastCommand(string file, bool last)
    {
        if (!File.Exists(file)) return null;
        var lines = File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));
        var line = last ? lines.LastOrDefault() : lines.FirstOrDefault();
        var parts = line?.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: >= 2 }) return null;
        return parts[0] is "pick" or "p" or "reword" or "r" or "edit" or "e" or "squash" or "s" or "fixup" or "f"
            ? (parts[0], parts[1])
            : null;
    }

    /// <summary>
    /// Moves files out of the working tree into the worktree's git folder (keeping their paths), so a rebase can
    /// write the committed versions. <see cref="RestoreAsync"/> puts them back.
    /// </summary>
    public static async Task<string> MoveAsideAsync(string worktree, IReadOnlyList<string> paths)
    {
        var folder = await GitPathAsync(worktree, AsideFolder);
        foreach (var path in paths)
        {
            var from = Path.Combine(worktree, path);
            if (!File.Exists(from)) continue;
            var to = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            // An older copy left aside earlier is replaced by the current one.
            File.Move(from, to, overwrite: true);
            RemoveEmptyFolders(Path.GetDirectoryName(from)!, worktree);
        }
        return folder;
    }

    /// <summary>
    /// Puts files moved aside back where they were, once nothing is in the way. Null when none were moved aside.
    /// </summary>
    public static async Task<RestoreResult?> RestoreAsync(string worktree)
    {
        var folder = await GitPathAsync(worktree, AsideFolder);
        if (!Directory.Exists(folder)) return null;
        var restored = new List<string>();
        var left = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            var to = Path.Combine(worktree, relative);
            if (File.Exists(to) || Directory.Exists(to))
            {
                left.Add(relative);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(file, to);
            restored.Add(relative);
            RemoveEmptyFolders(Path.GetDirectoryName(file)!, folder);
        }
        if (left.Count == 0 && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        return restored.Count == 0 && left.Count == 0 ? null : new RestoreResult(restored, left, folder);
    }

    /// <summary>Removes <paramref name="dir"/> and its parents while they're empty, stopping at <paramref name="root"/>.</summary>
    private static void RemoveEmptyFolders(string dir, string root)
    {
        var stop = Path.GetFullPath(root).TrimEnd('\\', '/');
        var current = Path.GetFullPath(dir).TrimEnd('\\', '/');
        while (current.Length > stop.Length && current.StartsWith(stop, StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
        {
            Directory.Delete(current);
            current = Path.GetDirectoryName(current)!;
        }
    }

    /// <summary>A path inside this worktree's own git folder.</summary>
    private static async Task<string> GitPathAsync(string worktree, string name)
    {
        var path = (await GitCli.RunAsync(worktree, "rev-parse", "--git-path", name)).StdOut.Trim();
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(worktree, path));
    }
}
