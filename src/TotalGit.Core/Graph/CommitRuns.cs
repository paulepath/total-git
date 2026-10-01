using TotalGit.Core.Git;

namespace TotalGit.Core.Graph;

/// <summary>
/// Finds runs of commits that can be folded into one row: consecutive rows of the graph on one line, each the only
/// parent of the row above, with no merge, no branch forking off, and no other branch's commit between them.
/// </summary>
public static class CommitRuns
{
    /// <summary>The fewest commits worth folding.</summary>
    public const int MinLength = 3;

    /// <summary>How far apart a rebase's rewritten commits may be written and still count as one rebase.</summary>
    private static readonly TimeSpan RebaseWindow = TimeSpan.FromMinutes(10);

    /// <summary>How much later than authored a commit must be written to count as rewritten.</summary>
    private static readonly TimeSpan Rewritten = TimeSpan.FromMinutes(2);

    /// <param name="commits">The history in the order the graph shows it, children before parents.</param>
    /// <returns>Every run of at least <see cref="MinLength"/> commits, each newest first.</returns>
    public static IReadOnlyList<CommitFold> Find(IReadOnlyList<CommitInfo> commits)
    {
        var bySha = new Dictionary<string, CommitInfo>(commits.Count);
        foreach (var c in commits) bySha.TryAdd(c.Sha, c);
        var children = new Dictionary<string, int>();
        foreach (var c in commits)
            foreach (var p in c.ParentShas)
                children[p] = children.GetValueOrDefault(p) + 1;

        bool Plain(CommitInfo c) => !c.IsWorkingTree && c.ParentShas.Count == 1;

        // Row i continues into row i + 1 when that row is its parent and nothing else hangs off it: the parent has
        // no other child (no branch forks there). Another branch's commit in between breaks the run.
        bool Continues(int i) =>
            i + 1 < commits.Count && Plain(commits[i]) && Plain(commits[i + 1])
            && commits[i].ParentShas[0] == commits[i + 1].Sha && children.GetValueOrDefault(commits[i + 1].Sha) == 1;

        var runs = new List<CommitFold>();
        for (var i = 0; i < commits.Count; i++)
        {
            if (!Plain(commits[i])) continue;
            var start = i;
            while (Continues(i)) i++;
            var run = commits.Skip(start).Take(i - start + 1).ToList();
            if (run.Count >= MinLength) runs.Add(Fold(run, RebasedTail(run, bySha) == run.Count));
        }
        return runs;
    }

    /// <summary>
    /// How many of a run's oldest commits (newest first) were rebased onto work done after them: each rewritten some
    /// time after it was authored, all in one go, and the commit they now sit on written after the earliest was authored.
    /// </summary>
    private static int RebasedTail(List<CommitInfo> run, IReadOnlyDictionary<string, CommitInfo> bySha)
    {
        var oldest = run[^1];
        if (oldest.CommitDate is not { } rebasedAt || oldest.ParentShas.Count == 0
            || !bySha.TryGetValue(oldest.ParentShas[0], out var onto) || onto.CommitDate is not { } ontoWritten)
            return 0;

        var count = 0;
        for (var i = run.Count - 1; i >= 0; i--)
        {
            var c = run[i];
            if (c.CommitDate is not { } written || written - c.AuthorDate < Rewritten || (written - rebasedAt).Duration() > RebaseWindow) break;
            count++;
        }
        if (count == 0) return 0;
        var earliest = run.Skip(run.Count - count).Min(c => c.AuthorDate);
        return ontoWritten > earliest ? count : 0;
    }

    /// <summary>The run through <paramref name="sha"/>, if it's on one.</summary>
    public static CommitFold? RunContaining(IReadOnlyList<CommitFold> runs, string sha) =>
        runs.FirstOrDefault(r => r.Shas.Contains(sha));

    private static CommitFold Fold(List<CommitInfo> run, bool rebased) => new(
        run.Select(c => c.Sha).ToArray(),
        run.GroupBy(c => c.AuthorEmail, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.First().AuthorName)
            .ToArray(),
        run.Min(c => c.AuthorDate),
        run.Max(c => c.AuthorDate),
        rebased);
}
