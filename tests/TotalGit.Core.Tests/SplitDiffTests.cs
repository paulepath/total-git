using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public class SplitDiffTests
{
    private static IReadOnlyList<SplitRow> Split(string patch) => SplitDiff.Build(UnifiedDiff.Parse(patch).Lines);

    [Fact]
    public void Context_lines_appear_on_both_sides()
    {
        var rows = Split("""
            @@ -5,2 +7,2 @@
             a
             b
            """);

        Assert.True(rows[0].IsHunk);
        Assert.Equal(5, rows[1].Left!.Value.OldLine);
        Assert.Equal(7, rows[1].Right!.Value.NewLine);
        Assert.Equal("b", rows[2].Right!.Value.Text);
    }

    [Fact]
    public void Removed_and_added_runs_are_paired_with_fillers()
    {
        var rows = Split("""
            @@ -1,4 +1,2 @@
            -r1
            -r2
            -r3
            +a1
             c
            """);

        Assert.Equal(5, rows.Count);
        Assert.Equal(("r1", "a1"), (rows[1].Left!.Value.Text, rows[1].Right!.Value.Text));
        Assert.Equal("r2", rows[2].Left!.Value.Text);
        Assert.Null(rows[2].Right);
        Assert.Null(rows[3].Right);
        Assert.Equal("c", rows[4].Left!.Value.Text);
    }

    [Fact]
    public void Pure_additions_have_empty_left_side()
    {
        var rows = Split("""
            @@ -0,0 +1,2 @@
            +x
            +y
            """);

        Assert.All(rows.Skip(1), r => Assert.Null(r.Left));
        Assert.Equal(2, rows[2].Right!.Value.NewLine);
    }

    [Fact]
    public void Multiple_hunks_stay_in_order()
    {
        var rows = Split("""
            @@ -1 +1 @@
            -a
            +b
            @@ -10 +10 @@
            -c
            +d
            """);

        Assert.Equal([true, false, true, false], rows.Select(r => r.IsHunk));
        Assert.Equal("d", rows[3].Right!.Value.Text);
    }

    [Fact]
    public void Removal_after_addition_starts_a_new_block()
    {
        var rows = Split("""
            @@ -1,2 +1,2 @@
            -a
            +b
            -c
            +d
            """);

        Assert.Equal(3, rows.Count);
        Assert.Equal(("c", "d"), (rows[2].Left!.Value.Text, rows[2].Right!.Value.Text));
    }

    [Fact]
    public void No_newline_marker_follows_its_side()
    {
        var rows = Split("""
            @@ -1 +1 @@
            -old
            \ No newline at end of file
            +new
            """);

        Assert.Equal(3, rows.Count);
        Assert.Equal(DiffLineKind.NoNewline, rows[2].Left!.Value.Kind);
        Assert.Null(rows[2].Right);
        Assert.Equal("new", rows[1].Right!.Value.Text);
    }
}
