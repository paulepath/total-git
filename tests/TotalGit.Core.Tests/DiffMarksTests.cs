using TotalGit.Core.Git;
using TotalGit.Core.Hosting;

namespace TotalGit.Core.Tests;

public class DiffMarksTests
{
    private const string Patch = """
        @@ -1,6 +1,6 @@
         a
        -b
        -c
        +B
         d
        +e
        -f
         g
        """;

    private static IReadOnlyList<DiffLine> Lines(string patch) => UnifiedDiff.Parse(patch).Lines;

    private static IReadOnlyList<SplitRowLines> Split(IReadOnlyList<DiffLine> lines) =>
        SplitRowIndex.Build(lines, SplitDiff.Build(lines));

    [Fact]
    public void Unified_runs_of_removed_and_added_lines_are_marked_by_line()
    {
        var marks = DiffMarks.Build(Lines(Patch), null, null);

        Assert.Equal(
            [
                new DiffMark(2, 2, DiffMarkKind.Removed),
                new DiffMark(4, 1, DiffMarkKind.Added),
                new DiffMark(6, 1, DiffMarkKind.Added),
                new DiffMark(7, 1, DiffMarkKind.Removed),
            ],
            marks);
    }

    [Fact]
    public void Side_by_side_a_replaced_line_marks_both_lanes_on_one_row()
    {
        var lines = Lines(Patch);
        var split = Split(lines);

        var marks = DiffMarks.Build(lines, split, null);

        // Rows: hunk, a, b|B, c|-, d, -|e, f|- (an added line before a removed one isn't a replacement), g.
        Assert.Equal(
            [
                new DiffMark(2, 2, DiffMarkKind.Removed),
                new DiffMark(2, 1, DiffMarkKind.Added),
                new DiffMark(5, 1, DiffMarkKind.Added),
                new DiffMark(6, 1, DiffMarkKind.Removed),
            ],
            marks);
    }

    [Fact]
    public void Hunks_and_context_lines_are_not_marked()
    {
        var lines = Lines("""
            @@ -1,2 +1,2 @@
             a
             b
            @@ -10,1 +10,1 @@
             c
            """);

        Assert.Empty(DiffMarks.Build(lines, null, null));
        Assert.Empty(DiffMarks.Build(lines, Split(lines), null));
    }

    [Fact]
    public void A_new_file_is_one_added_run()
    {
        var lines = Lines("""
            @@ -0,0 +1,3 @@
            +a
            +b
            +c
            """);

        Assert.Equal([new DiffMark(1, 3, DiffMarkKind.Added)], DiffMarks.Build(lines, null, null));
        Assert.Equal([new DiffMark(1, 3, DiffMarkKind.Added)], DiffMarks.Build(lines, Split(lines), null));
    }

    [Fact]
    public void Lines_changed_since_the_review_and_where_lines_went_are_marked()
    {
        // New-side numbers: a=1, B=2, d=3, e=4, g=5. Since the review B changed, and something above g went.
        var delta = new ReviewDelta(new HashSet<int> { 2 }, new HashSet<int> { 5 });

        var marks = DiffMarks.Build(Lines(Patch), null, delta).Where(m => m.Kind == DiffMarkKind.SinceReview);

        Assert.Equal([new DiffMark(4, 1, DiffMarkKind.SinceReview), new DiffMark(8, 1, DiffMarkKind.SinceReview)], marks);
    }

    [Fact]
    public void An_empty_diff_has_no_marks()
    {
        Assert.Empty(DiffMarks.Build([], null, null));
        Assert.Empty(DiffMarks.Build([], [], ReviewDelta.None));
    }
}
