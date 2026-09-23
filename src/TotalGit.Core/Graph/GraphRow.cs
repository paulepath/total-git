using TotalGit.Core.Git;

namespace TotalGit.Core.Graph;

/// <summary>Vertical anchor inside a row: top edge, node centre, or bottom edge.</summary>
public enum RowAnchor
{
    Top,
    Middle,
    Bottom,
}

/// <summary>A line drawn within a single row, from one lane/anchor to another.</summary>
public readonly record struct GraphSegment(int FromLane, RowAnchor From, int ToLane, RowAnchor To, int ColorIndex);

public sealed class GraphRow(CommitInfo commit, int lane, int colorIndex, IReadOnlyList<GraphSegment> segments)
{
    public CommitInfo Commit { get; } = commit;
    public int Lane { get; } = lane;
    public int ColorIndex { get; } = colorIndex;
    public IReadOnlyList<GraphSegment> Segments { get; } = segments;
}

public sealed class GraphLayoutResult(IReadOnlyList<GraphRow> rows, int laneCount)
{
    public IReadOnlyList<GraphRow> Rows { get; } = rows;
    public int LaneCount { get; } = laneCount;
}
