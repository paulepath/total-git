using TotalGit.Core.Git;
using TotalGit.Core.Graph;

namespace TotalGit.Core.Tests;

public class GraphLayoutTests
{
    private static CommitInfo C(string sha, params string[] parents) =>
        new(sha, parents, "A", "a@example.com", DateTimeOffset.UnixEpoch, sha);

    private static int[] Lanes(GraphLayoutResult r) => r.Rows.Select(x => x.Lane).ToArray();

    [Fact]
    public void Linear_history_stays_in_one_lane()
    {
        var r = GraphLayout.Compute([C("c", "b"), C("b", "a"), C("a")]);

        Assert.Equal([0, 0, 0], Lanes(r));
        Assert.Equal(1, r.LaneCount);
        Assert.All(r.Rows, row => Assert.Equal(r.Rows[0].ColorIndex, row.ColorIndex));
        // Root commit has only an incoming line, no outgoing one.
        Assert.DoesNotContain(r.Rows[2].Segments, s => s.From == RowAnchor.Middle);
    }

    [Fact]
    public void Branch_and_merge_uses_second_lane()
    {
        // m merges f into main; f and b both descend from a.
        var r = GraphLayout.Compute([C("m", "b", "f"), C("f", "a"), C("b", "a"), C("a")]);

        Assert.Equal([0, 1, 0, 0], Lanes(r));
        Assert.Equal(2, r.LaneCount);
        Assert.Contains(r.Rows[0].Segments, s => s is { FromLane: 0, From: RowAnchor.Middle, ToLane: 1, To: RowAnchor.Bottom });
        // Lane 1 converges back into lane 0 at the fork point.
        Assert.Contains(r.Rows[3].Segments, s => s is { FromLane: 1, From: RowAnchor.Top, ToLane: 0, To: RowAnchor.Middle });
        Assert.NotEqual(r.Rows[0].ColorIndex, r.Rows[1].ColorIndex);
    }

    [Fact]
    public void Two_branch_tips_sharing_a_parent_converge_at_parent()
    {
        var r = GraphLayout.Compute([C("x", "a"), C("y", "a"), C("a")]);

        Assert.Equal([0, 1, 0], Lanes(r));
        var converge = r.Rows[2].Segments.Where(s => s.To == RowAnchor.Middle).ToList();
        Assert.Equal(2, converge.Count);
        Assert.Contains(converge, s => s.FromLane == 1 && s.ToLane == 0);
    }

    [Fact]
    public void Octopus_merge_opens_a_lane_per_extra_parent()
    {
        var r = GraphLayout.Compute([C("m", "a", "b", "c"), C("c", "r"), C("b", "r"), C("a", "r"), C("r")]);

        Assert.Equal([0, 2, 1, 0, 0], Lanes(r));
        Assert.Equal(3, r.LaneCount);
        Assert.Equal(3, r.Rows[0].Segments.Count(s => s.From == RowAnchor.Middle));
    }

    [Fact]
    public void Multiple_roots_get_their_own_lanes()
    {
        var r = GraphLayout.Compute([C("m", "a", "z"), C("z"), C("a")]);

        Assert.Equal([0, 1, 0], Lanes(r));
        Assert.DoesNotContain(r.Rows[1].Segments, s => s.From == RowAnchor.Middle);
    }

    [Fact]
    public void Freed_lane_is_reused_by_later_branch()
    {
        // f1 branch ends (merges into a) before f2 branch starts.
        var r = GraphLayout.Compute([
            C("t2", "m2", "f2"), C("f2", "m2"), C("m2", "t1"),
            C("t1", "a", "f1"), C("f1", "a"), C("a"),
        ]);

        Assert.Equal([0, 1, 0, 0, 1, 0], Lanes(r));
        Assert.Equal(2, r.LaneCount);
    }

    [Fact]
    public void Missing_parent_leaves_lane_open_to_bottom()
    {
        var r = GraphLayout.Compute([C("b", "a")]);

        Assert.Contains(r.Rows[0].Segments, s => s is { From: RowAnchor.Middle, To: RowAnchor.Bottom });
    }
}
