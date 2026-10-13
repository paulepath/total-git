using TotalGit.Core.Hosting;

namespace TotalGit.Core.Git;

public enum DiffMarkKind
{
    Removed,
    Added,
    SinceReview,
}

/// <summary>A run of rows of a diff view that the scroll bar marks.</summary>
public sealed record DiffMark(int FirstRow, int RowCount, DiffMarkKind Kind);

/// <summary>Where the changes are in a diff view, by row, for the marks down its scroll bar.</summary>
public static class DiffMarks
{
    /// <summary>
    /// Runs of removed and of added rows, and of rows changed since the file was reviewed. <paramref name="split"/>
    /// is the side-by-side view's rows (from <see cref="SplitRowIndex.Build"/>); null for the unified view, where a
    /// row is a diff line. In the side-by-side view a replaced line is one row that's both removed and added.
    /// </summary>
    public static IReadOnlyList<DiffMark> Build(IReadOnlyList<DiffLine> lines, IReadOnlyList<SplitRowLines>? split, ReviewDelta? sinceReview)
    {
        var rowCount = split?.Count ?? lines.Count;
        var marks = new List<DiffMark>();
        var open = new Dictionary<DiffMarkKind, int>();

        bool Is(int line, DiffLineKind kind) => line >= 0 && lines[line].Kind == kind;

        bool Changed(int line)
        {
            if (sinceReview is null || line < 0 || lines[line] is not { NewLine: { } n } l || l.Kind == DiffLineKind.Removed) return false;
            return sinceReview.NewLines.Contains(n) || sinceReview.RemovedBefore.Contains(n);
        }

        void Track(DiffMarkKind kind, bool on, int row)
        {
            if (on && !open.ContainsKey(kind)) open[kind] = row;
            else if (!on && open.Remove(kind, out var first)) marks.Add(new DiffMark(first, row - first, kind));
        }

        for (var row = 0; row <= rowCount; row++)
        {
            var (left, right) = row == rowCount ? (-1, -1) : split is null ? (row, row) : (split[row].Left, split[row].Right);
            Track(DiffMarkKind.Removed, Is(left, DiffLineKind.Removed), row);
            Track(DiffMarkKind.Added, Is(right, DiffLineKind.Added), row);
            Track(DiffMarkKind.SinceReview, Changed(right), row);
        }
        return marks.OrderBy(m => m.FirstRow).ThenBy(m => m.Kind).ToList();
    }
}
