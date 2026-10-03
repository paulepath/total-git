namespace TotalGit.Core.Git;

/// <summary>A commit of a branch's own work: who wrote it and when.</summary>
public sealed record OwnCommit(string Sha, string Name, string Email, DateTimeOffset When);

/// <summary>Someone with commits on a branch.</summary>
public sealed record BranchAuthor(string Name, string Email, int Commits);

/// <summary>
/// Who a branch belongs to: the author of most of its own commits (those on no main line). With no commits of its
/// own (merged, or just created) <see cref="IsGuess"/> is set and the owner is whoever wrote its newest commit.
/// </summary>
/// <param name="OwnCommits">The branch's own commits (merges left out); 0 for a guess.</param>
/// <param name="Others">Everyone else with own commits, most first.</param>
/// <param name="SampleSha">One of the owner's commits, for looking up their avatar.</param>
public sealed record BranchOwner(BranchAuthor Owner, int OwnCommits, IReadOnlyList<BranchAuthor> Others, string SampleSha, bool IsGuess);

/// <summary>
/// Works out who branches belong to. Git doesn't record who made a branch, so its owner is the main author of the
/// commits only it has: walking back from its tip along first parents until reaching a commit on a main line (the
/// branches the rules mark as main lines: main, features, bugs…).
/// </summary>
public static class BranchOwnership
{
    /// <summary>Owners of <paramref name="branches"/> found in the loaded history, and the branches it didn't reach far enough for.</summary>
    /// <param name="mainLines">Every main-line branch, local and remote.</param>
    /// <param name="isMainLine">Whether a branch is a main line itself: it's then measured against the other main lines.</param>
    public static (Dictionary<string, BranchOwner> Owners, List<RefInfo> Unresolved) Compute(
        IReadOnlyList<CommitInfo> commits, IEnumerable<RefInfo> branches, IReadOnlyCollection<RefInfo> mainLines, Func<RefInfo, bool> isMainLine)
    {
        var mainLineTips = mainLines.Select(r => r.TargetSha).Distinct().ToList();
        var bySha = new Dictionary<string, CommitInfo>(StringComparer.Ordinal);
        foreach (var c in commits) if (!c.IsWorkingTree) bySha.TryAdd(c.Sha, c);

        var owners = new Dictionary<string, BranchOwner>(StringComparer.Ordinal);
        var unresolved = new List<RefInfo>();
        HashSet<string>? onMainLines = null;
        foreach (var branch in branches)
        {
            HashSet<string> stop;
            if (isMainLine(branch))
                stop = Reachable(bySha, OtherMainLineTips(mainLines, branch));
            else
                stop = onMainLines ??= Reachable(bySha, mainLineTips);

            var own = new List<OwnCommit>();
            var sha = branch.TargetSha;
            var complete = false;
            while (bySha.TryGetValue(sha, out var c))
            {
                if (stop.Contains(sha)) { complete = true; break; }
                if (!c.IsMerge) own.Add(Own(c));
                if (c.ParentShas.Count == 0) { complete = true; break; }
                sha = c.ParentShas[0];
            }
            if (!complete)
            {
                unresolved.Add(branch);
                continue;
            }
            // A main line with nothing of its own (main itself) is shared: no owner rather than a guess.
            if (own.Count == 0 && isMainLine(branch)) continue;
            var tip = bySha[branch.TargetSha];
            if (FromOwnCommits(own, Own(tip)) is { } owner) owners[branch.Name] = owner;
        }
        return (owners, unresolved);
    }

    /// <summary>The owner from a branch's own commits (newest first), else <paramref name="tip"/>'s author as a guess.</summary>
    public static BranchOwner? FromOwnCommits(IReadOnlyList<OwnCommit> own, OwnCommit? tip)
    {
        if (own.Count == 0)
            return tip is null ? null : new BranchOwner(new BranchAuthor(tip.Name, tip.Email, 0), 0, [], tip.Sha, IsGuess: true);

        // Most commits first; a tie goes to whoever committed most recently. Named as they last signed a commit.
        var authors = own
            .GroupBy(c => c.Email.Trim().ToLowerInvariant())
            .Select(g => (Latest: g.MaxBy(c => c.When)!, Count: g.Count()))
            .OrderByDescending(a => a.Count).ThenByDescending(a => a.Latest.When)
            .ToList();
        var first = authors[0];
        return new BranchOwner(
            new BranchAuthor(first.Latest.Name, first.Latest.Email, first.Count),
            own.Count,
            authors.Skip(1).Select(a => new BranchAuthor(a.Latest.Name, a.Latest.Email, a.Count)).ToList(),
            first.Latest.Sha,
            IsGuess: false);
    }

    /// <summary>
    /// The tips a main line is measured against: every other main line's (by name, so origin/main and main are the
    /// same line, while features/a sitting on main's tip has nothing of its own).
    /// </summary>
    public static List<string> OtherMainLineTips(IEnumerable<RefInfo> mainLines, RefInfo branch) => mainLines
        .Where(r => r.ShortName != branch.ShortName).Select(r => r.TargetSha).Distinct().ToList();

    private static OwnCommit Own(CommitInfo c) => new(c.Sha, c.AuthorName, c.AuthorEmail, c.CommitDate ?? c.AuthorDate);

    /// <summary>Every loaded commit reachable from <paramref name="tips"/> (all parents).</summary>
    private static HashSet<string> Reachable(Dictionary<string, CommitInfo> bySha, IEnumerable<string> tips)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(tips);
        while (stack.Count > 0)
        {
            var sha = stack.Pop();
            if (!seen.Add(sha) || !bySha.TryGetValue(sha, out var c)) continue;
            foreach (var p in c.ParentShas) if (!seen.Contains(p)) stack.Push(p);
        }
        return seen;
    }
}
