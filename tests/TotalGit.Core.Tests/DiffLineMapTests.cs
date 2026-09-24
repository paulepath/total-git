using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class DiffLineMapTests
{
    private static readonly DiffLine[] Lines = UnifiedDiff.Parse("""
        @@ -10,4 +10,4 @@
         keep
        -old one
        -old two
        +new one
         after
        @@ -40,3 +40,1 @@
         last kept
        -gone at end
        -gone too
        """.Replace("\r\n", "\n")).Lines.ToArray();

    [Theory]
    [InlineData(0, 10)]   // hunk header: first line of the hunk
    [InlineData(1, 10)]   // context
    [InlineData(2, 11)]   // removed: the next new line
    [InlineData(3, 11)]
    [InlineData(4, 11)]   // added
    [InlineData(5, 12)]
    [InlineData(7, 40)]
    [InlineData(8, 41)]   // removed at the end of a hunk: after the previous line
    [InlineData(9, 41)]
    public void Maps_rows_to_new_file_lines(int row, int expected) =>
        Assert.Equal(expected, DiffLineMap.TargetLine(Lines, row));

    [Fact]
    public void A_hunk_of_only_removals_maps_to_line_one()
    {
        var lines = UnifiedDiff.Parse("@@ -1,2 +0,0 @@\n-a\n-b\n").Lines;
        Assert.Equal(1, DiffLineMap.TargetLine(lines, 1));
        Assert.Null(DiffLineMap.TargetLine(lines, 5));
    }
}
