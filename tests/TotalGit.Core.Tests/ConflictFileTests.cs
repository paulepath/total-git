using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class ConflictFileTests
{
    private const string Text =
        "top\n" +
        "<<<<<<< HEAD\n" +
        "ours 1\n" +
        "ours 2\n" +
        "=======\n" +
        "theirs 1\n" +
        ">>>>>>> feature/x\n" +
        "middle\n" +
        "<<<<<<< HEAD\n" +
        "a\n" +
        "||||||| base\n" +
        "o\n" +
        "=======\n" +
        "b\n" +
        ">>>>>>> feature/x\n" +
        "bottom\n";

    [Fact]
    public void Parses_conflicts_with_labels_and_base()
    {
        var file = ConflictFile.Parse(Text);

        Assert.Equal(2, file.Conflicts.Count);
        Assert.Equal(["ours 1", "ours 2"], file.Conflicts[0].Ours);
        Assert.Equal(["theirs 1"], file.Conflicts[0].Theirs);
        Assert.Null(file.Conflicts[0].Base);
        Assert.Equal(["o"], file.Conflicts[1].Base!);
        Assert.Equal("HEAD", file.OursLabel);
        Assert.Equal("feature/x", file.TheirsLabel);
        Assert.Equal(5, file.Segments.Count);
    }

    [Fact]
    public void Unresolved_render_round_trips()
    {
        Assert.Equal(Text, ConflictFile.Parse(Text).Render([]));
        var crlf = Text.Replace("\n", "\r\n");
        Assert.Equal(crlf, ConflictFile.Parse(crlf).Render([]));
    }

    [Fact]
    public void Renders_choices()
    {
        var file = ConflictFile.Parse(Text);

        Assert.Equal("top\nours 1\nours 2\nmiddle\nb\nbottom\n", file.Render([ConflictChoice.Ours, ConflictChoice.Theirs]));
        Assert.Equal("top\ntheirs 1\nours 1\nours 2\nmiddle\na\nb\nbottom\n",
            file.Render([ConflictChoice.TheirsThenOurs, ConflictChoice.OursThenTheirs]));
        Assert.True(ConflictFile.HasMarkers(file.Render([ConflictChoice.Ours])));
        Assert.False(ConflictFile.HasMarkers(file.Render([ConflictChoice.Ours, ConflictChoice.Ours])));
    }

    [Fact]
    public void Side_reports_conflict_positions()
    {
        var theirs = ConflictFile.Parse(Text).Side(ours: false);

        Assert.Equal(["top", "theirs 1", "middle", "b", "bottom"], theirs.Lines);
        Assert.Equal([(1, 1), (3, 1)], theirs.Conflicts);
    }

    [Fact]
    public void Keeps_a_missing_final_newline_and_ignores_unclosed_markers()
    {
        const string text = "x\n<<<<<<< HEAD\ny";
        var file = ConflictFile.Parse(text);

        Assert.Empty(file.Conflicts);
        Assert.Equal(text, file.Render([]));
    }
}
