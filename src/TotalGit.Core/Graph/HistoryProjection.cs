using TotalGit.Core.Git;

namespace TotalGit.Core.Graph;

/// <summary>A run of commits shown as one row: its newest commit stands for all of them.</summary>
/// <param name="Shas">The run's commits, newest first (the first is the row's own commit).</param>
public sealed record CommitFold(IReadOnlyList<string> Shas, IReadOnlyList<string> Authors, DateTimeOffset From, DateTimeOffset To, bool IsRebased)
{
    public int Count => Shas.Count;
}

/// <summary>The commits to lay out after filtering and folding, and what became of the others.</summary>
/// <param name="ShownAs">For each hidden commit, the shown commit that stands in for it (its nearest shown descendant), if any.</param>
public sealed record ProjectedHistory(
    IReadOnlyList<CommitInfo> Commits,
    IReadOnlyDictionary<string, CommitFold> Folds,
    IReadOnlyDictionary<string, string> ShownAs,
    int Hidden);

/// <summary>
/// Hides commits from the history while keeping its shape: each shown commit's parents are rewritten to its nearest
/// shown ancestors, so the graph's lines run on across the hidden ones.
/// </summary>
public static class HistoryProjection
{
    /// <param name="commits">The loaded history, children before parents.</param>
    /// <param name="keep">Which commits the filters keep (WIP rows are always kept).</param>
    /// <param name="folds">Runs to show as one row, each newest first.</param>
    public static ProjectedHistory Project(IReadOnlyList<CommitInfo> commits, Func<CommitInfo, bool>? keep,
        IReadOnlyList<CommitFold>? folds = null)
    {
        var bySha = new Dictionary<string, CommitInfo>(commits.Count);
        foreach (var c in commits) bySha.TryAdd(c.Sha, c);

        // A fold hides all but its newest commit, and that one takes the oldest one's parents.
        var foldByTip = new Dictionary<string, CommitFold>();
        var foldedAway = new HashSet<string>();
        foreach (var fold in folds ?? [])
        {
            if (fold.Count < 2 || !bySha.ContainsKey(fold.Shas[0])) continue;
            foldByTip[fold.Shas[0]] = fold;
            foreach (var sha in fold.Shas.Skip(1)) foldedAway.Add(sha);
        }

        var shown = new HashSet<string>();
        foreach (var c in commits)
            if (!foldedAway.Contains(c.Sha) && (c.IsWorkingTree || keep is null || keep(c)))
                shown.Add(c.Sha);

        if (shown.Count == commits.Count && foldByTip.Count == 0)
            return new ProjectedHistory(commits, foldByTip, new Dictionary<string, string>(), 0);

        // Nearest shown ancestors of each hidden commit. Parents come after children, so walking the list backwards
        // resolves every parent before its children (no recursion, however long the hidden stretch). Parents outside
        // the loaded history are kept as they are, so their lines stay open as before.
        var nearest = new Dictionary<string, string[]>();
        IEnumerable<string> Nearest(string sha) =>
            shown.Contains(sha) || !nearest.TryGetValue(sha, out var through) ? [sha] : through;
        for (var i = commits.Count - 1; i >= 0; i--)
        {
            var c = commits[i];
            if (!shown.Contains(c.Sha)) nearest[c.Sha] = Distinct(c.ParentShas.SelectMany(Nearest));
        }

        var output = new List<CommitInfo>(shown.Count);
        foreach (var c in commits)
        {
            if (!shown.Contains(c.Sha)) continue;
            var parents = foldByTip.TryGetValue(c.Sha, out var fold) && bySha.TryGetValue(fold.Shas[^1], out var oldest)
                ? oldest.ParentShas
                : c.ParentShas;
            var rewritten = Distinct(parents.SelectMany(Nearest));
            output.Add(SameList(rewritten, c.ParentShas) ? c : c with { ParentShas = rewritten });
        }

        return new ProjectedHistory(output, foldByTip, ShownAs(commits, shown, foldByTip, bySha), commits.Count - output.Count);
    }

    /// <summary>
    /// For each hidden commit, the nearest shown commit above it on its line (a fold's commits map to the fold's row),
    /// so a branch label on a hidden commit can sit on the row that now stands for it.
    /// </summary>
    private static Dictionary<string, string> ShownAs(IReadOnlyList<CommitInfo> commits, HashSet<string> shown,
        Dictionary<string, CommitFold> foldByTip, Dictionary<string, CommitInfo> bySha)
    {
        var map = new Dictionary<string, string>();
        foreach (var (tip, fold) in foldByTip)
            foreach (var sha in fold.Shas.Skip(1))
                map[sha] = tip;

        // Children come first, so a hidden commit's first child (on its line) is already resolved when it's reached.
        foreach (var c in commits)
        {
            var standIn = shown.Contains(c.Sha) ? c.Sha : map.GetValueOrDefault(c.Sha);
            if (standIn is null || c.ParentShas.Count == 0) continue;
            var parent = c.ParentShas[0];
            if (!shown.Contains(parent) && bySha.ContainsKey(parent)) map.TryAdd(parent, standIn);
        }
        // A hidden branch tip has nothing shown above it, so it has no stand-in: putting its label further down
        // would claim the branch points at a commit it doesn't.
        return map;
    }

    private static string[] Distinct(IEnumerable<string> shas) => shas.Distinct(StringComparer.Ordinal).ToArray();

    private static bool SameList(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First == p.Second);
}
