using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public class SyntaxHighlighterTests
{
    [Theory]
    [InlineData("src/App.cs")]
    [InlineData("web/index.ts")]
    [InlineData("src/TotalGit.App/TotalGit.App.csproj")]
    [InlineData(".github/workflows/release.yml")]
    [InlineData("deploy/Dockerfile")]
    [InlineData("README.md")]
    [InlineData("scripts/build.ps1")]
    public void Finds_a_grammar_for_common_files(string path) => Assert.NotNull(SyntaxHighlighter.ForPath(path));

    [Theory]
    [InlineData("data.xyz")]
    [InlineData("LICENSE")]
    public void Unknown_types_are_plain(string path) => Assert.Null(SyntaxHighlighter.ForPath(path));

    [Fact]
    public void Keywords_and_strings_get_different_colours()
    {
        var line = "var name = \"total\";";
        var spans = SyntaxHighlighter.ForPath("a.cs")!.Highlight([line])[0];

        var keyword = ColourAt(spans, line.IndexOf("var", StringComparison.Ordinal));
        var text = ColourAt(spans, line.IndexOf("total", StringComparison.Ordinal));
        Assert.NotNull(keyword);
        Assert.NotNull(text);
        Assert.NotEqual(keyword, text);
    }

    [Fact]
    public void A_block_comment_carries_across_lines_until_a_restart()
    {
        string[] lines = ["/* start", "int middle = 1;", "end */", "/* again", "int fresh = 1;"];
        var highlighter = SyntaxHighlighter.ForPath("a.cs")!;
        var carried = highlighter.Highlight(lines);
        var comment = ColourAt(carried[0], 0);

        Assert.NotNull(comment);
        Assert.Equal(comment, ColourAt(carried[1], 0)); // "int" is inside the comment
        Assert.Equal(comment, ColourAt(carried[4], 0));

        // Starting afresh at line 4 (a new hunk), "int" is a keyword again.
        var restarted = highlighter.Highlight(lines, new HashSet<int> { 4 });
        Assert.NotEqual(comment, ColourAt(restarted[4], 0));
    }

    [Fact]
    public void Over_long_lines_are_plain()
    {
        var line = "var x = \"" + new string('a', SyntaxHighlighter.MaxLineLength) + "\";";
        Assert.Empty(SyntaxHighlighter.ForPath("a.cs")!.Highlight([line])[0]);
    }

    [Fact]
    public void Spans_lie_within_their_lines()
    {
        string[] lines = ["namespace A;", "", "public class B { private int _c = 4; } // done", "\tstring s = $\"{1}\";"];
        var all = SyntaxHighlighter.ForPath("a.cs")!.Highlight(lines);
        for (var i = 0; i < lines.Length; i++)
            foreach (var s in all[i])
            {
                Assert.True(s.Start >= 0 && s.Length > 0, $"line {i}: {s}");
                Assert.True(s.Start + s.Length <= lines[i].Length, $"line {i}: {s}");
            }
    }

    [Fact]
    public void A_diff_colours_each_side_as_its_own_file()
    {
        // The old side opens a comment the new side doesn't have, so only the removed line after it is a comment.
        DiffLine[] diff =
        [
            new(DiffLineKind.Hunk, null, null, "@@ -1,3 +1,2 @@"),
            new(DiffLineKind.Removed, 1, null, "/* old"),
            new(DiffLineKind.Added, null, 1, "int added = 1;"),
            new(DiffLineKind.Removed, 2, null, "int gone = 1; */"),
            new(DiffLineKind.Context, 3, 2, "int kept = 1;"),
        ];
        var highlighter = SyntaxHighlighter.ForPath("a.cs")!;
        var spans = highlighter.HighlightDiff(diff, wholeFile: false);
        var comment = ColourAt(spans[1], 0);

        Assert.Empty(spans[0]);
        Assert.Equal(comment, ColourAt(spans[3], 0));
        Assert.NotEqual(comment, ColourAt(spans[2], 0));
        Assert.NotEqual(comment, ColourAt(spans[4], 0));
        Assert.Equal(ColourAt(spans[2], 0), ColourAt(spans[4], 0));
    }

    private static uint? ColourAt(IReadOnlyList<SyntaxSpan> spans, int index) =>
        spans.Where(s => index >= s.Start && index < s.Start + s.Length).Select(s => (uint?)s.Argb).FirstOrDefault();
}
