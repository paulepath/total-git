using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class IntraLineDiffTests
{
    private static (string[] Old, string[] New) Changed(string oldText, string newText)
    {
        var lines = new DiffLine[]
        {
            new(DiffLineKind.Removed, 10, null, oldText),
            new(DiffLineKind.Added, null, 10, newText),
        };
        var highlights = IntraLineDiff.Compute(lines);
        return (Texts(lines[0], highlights), Texts(lines[1], highlights));
    }

    private static string[] Texts(DiffLine line, IntraLineHighlights highlights) =>
        highlights.For(line).Select(r => line.Text.Substring(r.Start, r.Length)).ToArray();

    [Fact]
    public void Highlights_added_words()
    {
        var (old, @new) = Changed("    [LmsKey.Learn1]: 'Accelerate',", "    [LmsKey.Learn1]: 'Accelerate (ASPS)',");
        Assert.Empty(old);
        Assert.Equal([" (ASPS)"], @new);

        (old, @new) = Changed("    [LmsKey.Learn2]: 'Learn 2',", "    [LmsKey.Learn2]: 'Prism Learn 2.0',");
        Assert.Empty(old);
        Assert.Equal(["Prism ", ".0"], @new);
    }

    [Fact]
    public void Highlights_a_replaced_word_on_both_sides()
    {
        var (old, @new) = Changed("        prism_UI: true,", "        prism_UI: false,");
        Assert.Equal(["true"], old);
        Assert.Equal(["false"], @new);

        (old, @new) = Changed("{panelHeader('Lms', SectionKeys.Lms, 'mt20')}", "{panelHeader('LMS Settings', SectionKeys.Lms, 'mt20')}");
        Assert.Equal(["Lms"], old);
        Assert.Equal(["LMS Settings"], @new);
    }

    [Fact]
    public void Highlights_text_around_a_kept_word()
    {
        var (old, @new) = Changed("label=\"Slug\"", "label=\"Richardson Sub Domain / Company Id (Slug)\"");
        Assert.Empty(old);
        Assert.Equal(["Richardson Sub Domain / Company Id ("], @new.Take(1));
        Assert.Equal([")"], @new.Skip(1));
    }

    [Fact]
    public void Highlights_changed_indentation()
    {
        var (old, @new) = Changed("  return x;", "    return x;");
        Assert.Equal(["  "], old);
        Assert.Equal(["    "], @new);
    }

    [Fact]
    public void Leaves_completely_different_lines_to_the_line_tint()
    {
        var (old, @new) = Changed("import { a } from './a';", "export default function render() {}");
        Assert.Empty(old);
        Assert.Empty(@new);
    }

    [Fact]
    public void Pairs_lines_in_order_within_a_block_and_ignores_context()
    {
        var lines = new DiffLine[]
        {
            new(DiffLineKind.Context, 1, 1, "const a = 1;"),
            new(DiffLineKind.Removed, 2, null, "const b = 2;"),
            new(DiffLineKind.Removed, 3, null, "const c = 3;"),
            new(DiffLineKind.Added, null, 2, "const b = 20;"),
            new(DiffLineKind.Context, 4, 3, "const d = 4;"),
            new(DiffLineKind.Added, null, 4, "const e = 5;"),
        };
        var h = IntraLineDiff.Compute(lines);

        Assert.Empty(h.For(lines[0]));
        Assert.Equal(["2"], Texts(lines[1], h));
        Assert.Equal(["20"], Texts(lines[3], h));
        Assert.Empty(h.For(lines[2])); // no partner line
        Assert.Empty(h.For(lines[5]));
    }
}
