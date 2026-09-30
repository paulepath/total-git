using TotalGit.Core.Git;

namespace TotalGit.Core.Hosting;

/// <summary>Where a file's review threads go in its diff.</summary>
/// <param name="ByLine">Threads to show below a line, keyed by index into the diff's lines.</param>
/// <param name="Unplaced">Threads on the file that can't be shown in the diff: outdated, or on lines it doesn't show.</param>
public sealed record ThreadPlacement(IReadOnlyDictionary<int, IReadOnlyList<ReviewThread>> ByLine, IReadOnlyList<ReviewThread> Unplaced);

public static class ThreadAnchoring
{
    /// <summary>
    /// Places a file's threads on the lines they were made on. A thread on the right (new) side matches the line
    /// with that new line number; on the left (old) side, the one with that old line number. Outdated threads, and
    /// threads on lines outside the diff's hunks, are returned as unplaced.
    /// </summary>
    /// <param name="path">The file's path in the pull request (the new path for a renamed file).</param>
    public static ThreadPlacement Place(IReadOnlyList<DiffLine> lines, IEnumerable<ReviewThread> threads, string path)
    {
        var right = new Dictionary<int, int>();
        var left = new Dictionary<int, int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Kind is DiffLineKind.Context or DiffLineKind.Added && line.NewLine is { } n) right.TryAdd(n, i);
            if (line.Kind is DiffLineKind.Context or DiffLineKind.Removed && line.OldLine is { } o) left.TryAdd(o, i);
        }

        var byLine = new Dictionary<int, List<ReviewThread>>();
        var unplaced = new List<ReviewThread>();
        foreach (var thread in threads.Where(t => t.Anchor.Path == path))
        {
            var index = thread.IsOutdated || thread.Anchor.Line is not { } lineNo ? (int?)null
                : (thread.Anchor.Side == DiffSide.Right ? right : left).TryGetValue(lineNo, out var i) ? i : null;
            if (index is { } at)
            {
                if (!byLine.TryGetValue(at, out var list)) byLine[at] = list = [];
                list.Add(thread);
            }
            else
            {
                unplaced.Add(thread);
            }
        }
        return new ThreadPlacement(byLine.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ReviewThread>)kv.Value), unplaced);
    }
}
