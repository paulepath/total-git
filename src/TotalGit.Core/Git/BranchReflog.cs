namespace TotalGit.Core.Git;

/// <summary>
/// What a local branch's reflog says about where it came from. A branch is only a pointer, so git records no start for
/// it; but creating one writes a single "branch: Created from …" entry, and every later move (commit, reset, rebase,
/// pull, rename) adds another.
/// </summary>
public static class BranchReflog
{
    private const string CreatedPrefix = "branch: Created from ";

    /// <summary>
    /// Whether the branch was created here and hasn't moved since (no commits of its own yet), and what it was created
    /// from: a branch name, <c>HEAD</c> or a SHA, as given when it was created. Not new when there's no reflog (a
    /// fetched or cloned branch) or it can't be read.
    /// </summary>
    public static (bool IsNew, string? CreatedFrom) ReadCreation(string commonGitDir, string branch, string tipSha)
    {
        var path = Path.Combine(commonGitDir, "logs", "refs", "heads", branch.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(path)) return (false, null);
            using var reader = new StreamReader(path);
            var first = reader.ReadLine();
            if (first is null || reader.ReadLine() is { Length: > 0 }) return (false, null);
            return Parse(first, tipSha);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, null);
        }
    }

    /// <summary>One reflog line: <c>old new Name &lt;email&gt; time zone\tmessage</c>.</summary>
    private static (bool IsNew, string? CreatedFrom) Parse(string line, string tipSha)
    {
        var tab = line.IndexOf('\t');
        if (tab < 0) return (false, null);
        var shas = line[..tab].Split(' ', 3);
        var message = line[(tab + 1)..].TrimEnd();
        if (shas.Length < 2 || !string.Equals(shas[1], tipSha, StringComparison.OrdinalIgnoreCase)
            || !message.StartsWith(CreatedPrefix, StringComparison.Ordinal))
            return (false, null);
        var from = message[CreatedPrefix.Length..].Trim();
        return (true, from.Length > 0 ? from : null);
    }
}
