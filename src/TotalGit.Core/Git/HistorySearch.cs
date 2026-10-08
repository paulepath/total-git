namespace TotalGit.Core.Git;

/// <summary>Searches only the supplied history, in its existing order, without loading any more commits.</summary>
public static class HistorySearch
{
    public static IReadOnlyList<CommitInfo> Find(IEnumerable<CommitInfo> loaded, string query)
    {
        var text = query.Trim();
        if (text.Length == 0) return [];
        return loaded.Where(c => !c.IsWorkingTree && (
            c.Sha.StartsWith(text, StringComparison.OrdinalIgnoreCase)
            || (c.FullMessage ?? c.MessageShort).Contains(text, StringComparison.OrdinalIgnoreCase)
            || c.AuthorName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || c.AuthorEmail.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public static int Move(int current, int count, bool previous = false) => count <= 0 ? -1
        : current < 0 ? (previous ? count - 1 : 0)
        : (current + (previous ? -1 : 1) + count) % count;
}
