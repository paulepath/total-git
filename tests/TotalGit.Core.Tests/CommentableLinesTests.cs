using TotalGit.Core.Git;
using TotalGit.Core.Hosting;

namespace TotalGit.Core.Tests;

public class CommentableLinesTests
{
    private static IReadOnlyList<DiffLine> Lines(string patch) => UnifiedDiff.Parse(patch).Lines;

    [Fact]
    public void In_the_whole_file_only_lines_within_three_of_a_change_take_comments()
    {
        // Lines 1-8 unchanged, two added after 8, then 9-16 unchanged.
        var lines = Lines("""
            @@ -1,16 +1,18 @@
             l1
             l2
             l3
             l4
             l5
             l6
             l7
             l8
            +n1
            +n2
             l9
             l10
             l11
             l12
             l13
             l14
             l15
             l16
            """);

        var commentable = CommentableLines.Build(lines);

        var newLines = lines.Select((l, i) => (l, i)).Where(x => commentable[x.i]).Select(x => x.l.NewLine).ToList();
        Assert.Equal([6, 7, 8, 9, 10, 11, 12, 13], newLines); // l6-l8, the two added, l9-l11 (new 11-13)
        Assert.False(commentable[0]); // the hunk header
    }

    [Fact]
    public void Changes_close_together_join_up_and_removed_lines_take_comments()
    {
        var lines = Lines("""
            @@ -1,12 +1,12 @@
             a
            -b
            +B
             c
             d
             e
             f
             g
             h
             i
            -j
            +J
             k
            """);

        var commentable = CommentableLines.Build(lines);

        // Seven unchanged lines between the changes: three after the first, three before the second, one too far.
        Assert.Equal(["a", "b", "B", "c", "d", "e", "g", "h", "i", "j", "J", "k"],
            lines.Select((l, i) => (l, i)).Where(x => commentable[x.i]).Select(x => x.l.Text).ToList());
    }

    [Fact]
    public void An_ordinary_diff_is_all_commentable()
    {
        var lines = Lines("""
            @@ -10,7 +10,7 @@
             a
             b
             c
            -d
            +D
             e
             f
             g
            @@ -40,4 +40,5 @@
             w
             x
             y
            +z
            """);

        var commentable = CommentableLines.Build(lines);

        Assert.All(lines.Select((l, i) => (l, i)).Where(x => x.l.Kind != DiffLineKind.Hunk), x => Assert.True(commentable[x.i]));
    }
}
