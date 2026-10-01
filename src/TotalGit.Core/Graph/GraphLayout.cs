using TotalGit.Core.Git;

namespace TotalGit.Core.Graph;

/// <summary>
/// Assigns commits to lanes and produces the line segments for each row. Commits must be in
/// topological order (children before parents). Lanes keep a fixed column for their lifetime
/// so branches do not shift sideways as others end.
/// </summary>
public static class GraphLayout
{
    public const int PaletteSize = 10;

    public static GraphLayoutResult Compute(IReadOnlyList<CommitInfo> commits, IReadOnlySet<string>? trunk = null)
    {
        var builder = new GraphLayoutBuilder(trunk);
        builder.Append(commits);
        return builder.ToResult();
    }
}

/// <summary>
/// Incremental form of <see cref="GraphLayout"/>: keeps lane state between <see cref="Append"/>
/// calls so further pages of history extend the graph without recomputing earlier rows.
/// </summary>
/// <param name="trunk">
/// Commits on a main-line branch's first-parent line: the line from such a commit down to its first parent, when
/// that is one too, is marked <see cref="GraphSegment.Trunk"/>.
/// </param>
public sealed class GraphLayoutBuilder(IReadOnlySet<string>? trunk = null)
{
    private sealed class Lane(string expectedSha, int color)
    {
        public string ExpectedSha { get; set; } = expectedSha;
        public int Color { get; } = color;

        /// <summary>The lane carries a main-line branch from one of its commits down to the next.</summary>
        public bool Trunk { get; set; }
    }

    private readonly List<Lane?> _lanes = [];
    private readonly List<GraphRow> _rows = [];
    private int _nextColor;
    private int _maxLanes;

    public int RowCount => _rows.Count;

    public GraphLayoutResult ToResult() => new(_rows.ToArray(), _maxLanes);

    public void Append(IEnumerable<CommitInfo> commits)
    {
        foreach (var commit in commits) _rows.Add(Layout(commit));
    }

    private GraphRow Layout(CommitInfo commit)
    {
        var lanes = _lanes;
        var segments = new List<GraphSegment>();

        var converging = new List<int>();
        for (var i = 0; i < lanes.Count; i++)
            if (lanes[i]?.ExpectedSha == commit.Sha) converging.Add(i);

        int nodeLane;
        int nodeColor;
        if (converging.Count > 0)
        {
            // A main-line branch keeps its lane where others join it, so its line stays straight.
            nodeLane = converging.FirstOrDefault(i => lanes[i]!.Trunk, converging[0]);
            nodeColor = lanes[nodeLane]!.Color;
        }
        else
        {
            nodeLane = FirstFree(exclude: null);
            nodeColor = _nextColor++ % GraphLayout.PaletteSize;
            Set(nodeLane, new Lane(commit.Sha, nodeColor));
        }

        // Top half: converging lanes curve into the node, all others pass straight through.
        for (var i = 0; i < lanes.Count; i++)
        {
            var lane = lanes[i];
            if (lane is null || (i == nodeLane && converging.Count == 0)) continue;
            segments.Add(converging.Contains(i)
                ? new GraphSegment(i, RowAnchor.Top, nodeLane, RowAnchor.Middle, lane.Color, lane.Trunk && i == nodeLane)
                : new GraphSegment(i, RowAnchor.Top, i, RowAnchor.Bottom, lane.Color, lane.Trunk));
        }

        var freedThisRow = new HashSet<int>();
        foreach (var i in converging.Where(i => i != nodeLane))
        {
            lanes[i] = null;
            freedThisRow.Add(i);
        }

        // Bottom half: first parent continues in the node's lane, extra parents branch out.
        var parents = commit.ParentShas;
        if (parents.Count == 0)
        {
            lanes[nodeLane] = null;
        }
        else
        {
            var onTrunk = trunk is not null && trunk.Contains(commit.Sha) && trunk.Contains(parents[0]);
            lanes[nodeLane]!.ExpectedSha = parents[0];
            lanes[nodeLane]!.Trunk = onTrunk;
            segments.Add(new GraphSegment(nodeLane, RowAnchor.Middle, nodeLane, RowAnchor.Bottom, nodeColor, onTrunk));

            foreach (var parent in parents.Skip(1))
            {
                var existing = -1;
                for (var i = 0; i < lanes.Count; i++)
                    if (i != nodeLane && lanes[i]?.ExpectedSha == parent) { existing = i; break; }

                if (existing >= 0)
                {
                    segments.Add(new GraphSegment(nodeLane, RowAnchor.Middle, existing, RowAnchor.Bottom, lanes[existing]!.Color));
                    continue;
                }

                var target = FirstFree(freedThisRow);
                var color = _nextColor++ % GraphLayout.PaletteSize;
                Set(target, new Lane(parent, color));
                segments.Add(new GraphSegment(nodeLane, RowAnchor.Middle, target, RowAnchor.Bottom, color));
            }
        }

        _maxLanes = Math.Max(_maxLanes, lanes.Count);
        while (lanes.Count > 0 && lanes[^1] is null) lanes.RemoveAt(lanes.Count - 1);

        return new GraphRow(commit, nodeLane, nodeColor, segments);
    }

    private int FirstFree(HashSet<int>? exclude)
    {
        for (var i = 0; i < _lanes.Count; i++)
            if (_lanes[i] is null && (exclude is null || !exclude.Contains(i))) return i;
        return _lanes.Count;
    }

    private void Set(int index, Lane lane)
    {
        while (_lanes.Count <= index) _lanes.Add(null);
        _lanes[index] = lane;
    }
}
