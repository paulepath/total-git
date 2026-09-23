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

    private sealed class Lane(string expectedSha, int color)
    {
        public string ExpectedSha { get; set; } = expectedSha;
        public int Color { get; } = color;
    }

    public static GraphLayoutResult Compute(IReadOnlyList<CommitInfo> commits)
    {
        var lanes = new List<Lane?>();
        var rows = new List<GraphRow>(commits.Count);
        var nextColor = 0;
        var maxLanes = 0;

        foreach (var commit in commits)
        {
            var segments = new List<GraphSegment>();

            var converging = new List<int>();
            for (var i = 0; i < lanes.Count; i++)
                if (lanes[i]?.ExpectedSha == commit.Sha) converging.Add(i);

            int nodeLane;
            int nodeColor;
            if (converging.Count > 0)
            {
                nodeLane = converging[0];
                nodeColor = lanes[nodeLane]!.Color;
            }
            else
            {
                nodeLane = FirstFree(lanes, exclude: null);
                nodeColor = nextColor++ % PaletteSize;
                Set(lanes, nodeLane, new Lane(commit.Sha, nodeColor));
            }

            // Top half: converging lanes curve into the node, all others pass straight through.
            for (var i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                if (lane is null || (i == nodeLane && converging.Count == 0)) continue;
                segments.Add(converging.Contains(i)
                    ? new GraphSegment(i, RowAnchor.Top, nodeLane, RowAnchor.Middle, lane.Color)
                    : new GraphSegment(i, RowAnchor.Top, i, RowAnchor.Bottom, lane.Color));
            }

            var freedThisRow = new HashSet<int>();
            foreach (var i in converging.Skip(1))
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
                lanes[nodeLane]!.ExpectedSha = parents[0];
                segments.Add(new GraphSegment(nodeLane, RowAnchor.Middle, nodeLane, RowAnchor.Bottom, nodeColor));

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

                    var target = FirstFree(lanes, freedThisRow);
                    var color = nextColor++ % PaletteSize;
                    Set(lanes, target, new Lane(parent, color));
                    segments.Add(new GraphSegment(nodeLane, RowAnchor.Middle, target, RowAnchor.Bottom, color));
                }
            }

            maxLanes = Math.Max(maxLanes, lanes.Count);
            while (lanes.Count > 0 && lanes[^1] is null) lanes.RemoveAt(lanes.Count - 1);

            rows.Add(new GraphRow(commit, nodeLane, nodeColor, segments));
        }

        return new GraphLayoutResult(rows, maxLanes);
    }

    private static int FirstFree(List<Lane?> lanes, HashSet<int>? exclude)
    {
        for (var i = 0; i < lanes.Count; i++)
            if (lanes[i] is null && (exclude is null || !exclude.Contains(i))) return i;
        return lanes.Count;
    }

    private static void Set(List<Lane?> lanes, int index, Lane lane)
    {
        while (lanes.Count <= index) lanes.Add(null);
        lanes[index] = lane;
    }
}
