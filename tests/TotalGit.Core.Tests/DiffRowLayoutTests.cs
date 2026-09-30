using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public class DiffRowLayoutTests
{
    private const double H = 19;

    [Fact]
    public void Without_extras_rows_are_evenly_spaced()
    {
        var layout = new DiffRowLayout(100, H);

        Assert.Equal(100 * H, layout.TotalHeight);
        for (var row = 0; row < 100; row++)
        {
            Assert.Equal(row * H, layout.TopOf(row));
            Assert.Equal(row * H + H, layout.GapTopOf(row));
        }
        Assert.Equal((0, false), layout.RowAt(0));
        Assert.Equal((0, false), layout.RowAt(H - 0.01));
        Assert.Equal((1, false), layout.RowAt(H));
        Assert.Equal((52, false), layout.RowAt(52 * H + 7));
    }

    [Fact]
    public void Positions_outside_the_content_clamp()
    {
        var layout = new DiffRowLayout(10, H);
        layout.SetExtra(9, 50);

        Assert.Equal((0, false), layout.RowAt(-5));
        Assert.Equal((9, false), layout.RowAt(layout.TotalHeight));
        Assert.Equal((9, false), layout.RowAt(10_000));
    }

    [Fact]
    public void An_empty_layout_has_no_height()
    {
        var layout = new DiffRowLayout(0, H);

        Assert.Equal(0, layout.TotalHeight);
        Assert.Equal((0, false), layout.RowAt(12));
    }

    [Fact]
    public void Extra_space_pushes_down_the_rows_below()
    {
        var layout = new DiffRowLayout(10, H);
        layout.SetExtra(0, 40);
        layout.SetExtra(4, 30);
        layout.SetExtra(9, 20);

        Assert.Equal(10 * H + 90, layout.TotalHeight);
        Assert.Equal(0, layout.TopOf(0));
        Assert.Equal(H + 40, layout.TopOf(1));
        Assert.Equal(4 * H + 40, layout.TopOf(4));
        Assert.Equal(5 * H + 70, layout.TopOf(5));
        Assert.Equal(9 * H + 70, layout.TopOf(9));
        Assert.Equal(9 * H + 70 + H, layout.GapTopOf(9));
    }

    [Fact]
    public void Positions_in_extra_space_belong_to_the_row_above()
    {
        var layout = new DiffRowLayout(10, H);
        layout.SetExtra(0, 40);
        layout.SetExtra(4, 30);
        layout.SetExtra(9, 20);

        Assert.Equal((0, false), layout.RowAt(H - 0.5));
        Assert.Equal((0, true), layout.RowAt(H));
        Assert.Equal((0, true), layout.RowAt(H + 39.9));
        Assert.Equal((1, false), layout.RowAt(H + 40));
        Assert.Equal((4, false), layout.RowAt(layout.TopOf(4)));
        Assert.Equal((4, true), layout.RowAt(layout.GapTopOf(4) + 29));
        Assert.Equal((5, false), layout.RowAt(layout.GapTopOf(4) + 30));
        Assert.Equal((9, true), layout.RowAt(layout.GapTopOf(9) + 1));
        Assert.Equal((9, false), layout.RowAt(layout.TotalHeight + 1));
    }

    [Fact]
    public void Extras_can_be_replaced_and_removed()
    {
        var layout = new DiffRowLayout(10, H);
        Assert.True(layout.SetExtras(new Dictionary<int, double> { [2] = 10, [3] = 5, [20] = 99 }));
        Assert.Equal(10 * H + 15, layout.TotalHeight);
        Assert.False(layout.SetExtras(new Dictionary<int, double> { [3] = 5, [2] = 10 }));

        layout.SetExtra(2, 0);
        Assert.Equal(10 * H + 5, layout.TotalHeight);
        Assert.Equal(0, layout.ExtraOf(2));
        Assert.True(layout.SetExtras(new Dictionary<int, double>()));
        Assert.Equal(10 * H, layout.TotalHeight);
        Assert.Equal((3, false), layout.RowAt(3 * H + 1));
    }

    [Fact]
    public void Split_rows_know_their_line_indexes()
    {
        var lines = UnifiedDiff.Parse("""
            @@ -1,3 +1,3 @@
             c
            -r1
            \ No newline at end of file
            +a1
            +a2
            \ No newline at end of file
            """).Lines;
        var rows = SplitDiff.Build(lines);

        var index = SplitRowIndex.Build(lines, rows);

        Assert.Equal(
            [new(0, 0), new(1, 1), new(2, 4), new(3, 5), new(-1, 6)],
            index);
    }

    [Fact]
    public void A_marker_after_context_is_on_both_sides()
    {
        var lines = UnifiedDiff.Parse("""
            @@ -1,2 +1,2 @@
            -r
            \ No newline at end of file
            +a
             c
            \ No newline at end of file
            """).Lines;
        var rows = SplitDiff.Build(lines);

        var index = SplitRowIndex.Build(lines, rows);

        Assert.Equal([new(0, 0), new(1, 3), new(2, -1), new(4, 4), new(5, 5)], index);
    }
}
