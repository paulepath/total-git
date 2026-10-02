namespace TotalGit.Core.Git;

/// <summary>
/// What a repository folder has checked out, read straight from its HEAD file (fast enough for a list of recent
/// repositories, without opening each one).
/// </summary>
public static class GitHead
{
    /// <summary>
    /// The checked-out branch's name, a short commit id when HEAD is detached, or null when the folder isn't a
    /// repository (or can't be read).
    /// </summary>
    public static string? Read(string folder)
    {
        try
        {
            var gitDir = GitDir(folder);
            if (gitDir is null) return null;
            var head = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(head)) return null;
            var text = File.ReadAllText(head).Trim();
            const string prefix = "ref: refs/heads/";
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return text[prefix.Length..];
            if (text.StartsWith("ref: ", StringComparison.Ordinal)) return text[5..];
            return text.Length >= 7 ? text[..7] : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The folder's git directory: its .git folder, or where a .git file points (a worktree or submodule).</summary>
    private static string? GitDir(string folder)
    {
        var dotGit = Path.Combine(folder, ".git");
        if (Directory.Exists(dotGit)) return dotGit;
        if (File.Exists(dotGit))
        {
            var line = File.ReadAllText(dotGit).Trim();
            const string prefix = "gitdir:";
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var path = line[prefix.Length..].Trim();
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(folder, path));
        }
        // A bare repository opened directly.
        return File.Exists(Path.Combine(folder, "HEAD")) && Directory.Exists(Path.Combine(folder, "refs")) ? folder : null;
    }
}
