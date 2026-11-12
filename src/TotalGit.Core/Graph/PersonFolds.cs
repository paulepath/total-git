using TotalGit.Core.Git;

namespace TotalGit.Core.Graph;

/// <summary>One person's commits that can be folded out of the graph, newest first.</summary>
public sealed record PersonFold(string Email, string Name, IReadOnlyList<CommitInfo> Commits);

/// <summary>
/// Finds the commits to hide when a person's branches are folded: their commits that nobody else has built on. A
/// commit stays when anything kept descends from it (someone else's commit, or a pinned one such as HEAD or a main
/// branch's tip), so a branch someone else has committed to stays whole, and so does their work already merged.
/// </summary>
public static class PersonFolds
{
    /// <param name="commits">The loaded history, children before parents.</param>
    /// <param name="emails">The folded people's emails (compared as the set compares them).</param>
    /// <param name="pinned">Commits to keep whoever wrote them, along with everything they're built on.</param>
    public static IReadOnlyList<PersonFold> Find(IReadOnlyList<CommitInfo> commits, IReadOnlySet<string> emails, IReadOnlySet<string> pinned)
    {
        if (emails.Count == 0) return [];

        // Children come first, so by the time a commit is reached every child has said whether it needs it.
        var needed = new HashSet<string>(StringComparer.Ordinal);
        var byEmail = new Dictionary<string, List<CommitInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in commits)
        {
            if (c.IsPlaceholder || !emails.Contains(c.AuthorEmail) || pinned.Contains(c.Sha) || needed.Contains(c.Sha))
            {
                foreach (var p in c.ParentShas) needed.Add(p);
                continue;
            }
            if (!byEmail.TryGetValue(c.AuthorEmail, out var list)) byEmail[c.AuthorEmail] = list = [];
            list.Add(c);
        }
        return byEmail.Select(kv => new PersonFold(kv.Key, kv.Value[0].AuthorName, kv.Value)).ToList();
    }
}
