using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public class UnifiedDiffTests
{
    private const string Patch = """
        diff --git a/f.txt b/f.txt
        index 1111111..2222222 100644
        --- a/f.txt
        +++ b/f.txt
        @@ -1,3 +1,3 @@
         one
        -two
        +TWO
         three
        @@ -10,2 +10,3 @@ section
         ten
        +ten and a half
         eleven
        \ No newline at end of file

        """;

    [Fact]
    public void Parses_hunks_with_line_numbers()
    {
        var (lines, truncated) = UnifiedDiff.Parse(Patch);

        Assert.False(truncated);
        Assert.Equal(
            [DiffLineKind.Hunk, DiffLineKind.Context, DiffLineKind.Removed, DiffLineKind.Added, DiffLineKind.Context,
             DiffLineKind.Hunk, DiffLineKind.Context, DiffLineKind.Added, DiffLineKind.Context, DiffLineKind.NoNewline],
            lines.Select(l => l.Kind));

        Assert.Equal(new DiffLine(DiffLineKind.Removed, 2, null, "two"), lines[2]);
        Assert.Equal(new DiffLine(DiffLineKind.Added, null, 2, "TWO"), lines[3]);
        Assert.Equal(new DiffLine(DiffLineKind.Context, 3, 3, "three"), lines[4]);
        Assert.Equal(new DiffLine(DiffLineKind.Added, null, 11, "ten and a half"), lines[7]);
        Assert.Equal(new DiffLine(DiffLineKind.Context, 11, 12, "eleven"), lines[8]);
    }

    [Fact]
    public void Truncates_at_limit()
    {
        var (lines, truncated) = UnifiedDiff.Parse(Patch, maxLines: 4);

        Assert.True(truncated);
        Assert.Equal(4, lines.Count);
    }

    [Fact]
    public void Handles_crlf()
    {
        var (lines, _) = UnifiedDiff.Parse(Patch.Replace("\n", "\r\n"));

        Assert.Equal("TWO", lines[3].Text);
    }
}
