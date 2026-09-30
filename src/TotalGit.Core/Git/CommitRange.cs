namespace TotalGit.Core.Git;

/// <summary>A run of consecutive commits picked in the graph, newest first, or why the pick isn't one.</summary>
public sealed record CommitRangeResult(IReadOnlyList<CommitInfo> Commits, string? Error)
{
    public bool IsValid => Error is null;
    public CommitInfo Newest => Commits[0];
    public CommitInfo Oldest => Commits[^1];
}

public static class CommitRange
{
    /// <summary>
    /// The commits from one of <paramref name="a"/> and <paramref name="b"/> back to the other along first parents
    /// (either order), newest first. Fails when they aren't on one line of history, when a merge commit is in the
    /// range (rebasing merges isn't supported), or when the WIP row is picked.
    /// </summary>
    /// <remarks>
    /// History loads newest first in topological order, so every commit between two loaded commits of one line is
    /// loaded too: a walk that runs off the loaded commits means the two aren't on one line.
    /// </remarks>
    public static CommitRangeResult Resolve(IReadOnlyDictionary<string, CommitInfo> bySha, string a, string b)
    {
        if (!bySha.TryGetValue(a, out var ca) || !bySha.TryGetValue(b, out var cb))
            return Fail("The selection goes past the loaded history. Scroll down to load more commits, then try again.");
        if (ca.IsWorkingTree || cb.IsWorkingTree)
            return Fail("The selection includes uncommitted changes (the WIP row). Select commits only.");

        var walk = Walk(bySha, a, b) ?? Walk(bySha, b, a);
        if (walk is null)
            return Fail("Those commits aren't on one line of history. Select a run of commits on a single branch.");
        if (walk.FirstOrDefault(c => c.ParentShas.Count > 1) is { } merge)
            return Fail($"The selection includes a merge commit ({merge.ShortSha}). Rebasing merge commits isn't supported.");
        return new CommitRangeResult(walk, null);
    }

    private static List<CommitInfo>? Walk(IReadOnlyDictionary<string, CommitInfo> bySha, string from, string to)
    {
        var list = new List<CommitInfo>();
        for (var sha = from; bySha.TryGetValue(sha, out var commit);)
        {
            list.Add(commit);
            if (sha == to) return list;
            if (commit.ParentShas.Count == 0) return null;
            sha = commit.ParentShas[0];
        }
        return null;
    }

    private static CommitRangeResult Fail(string error) => new([], error);
}
