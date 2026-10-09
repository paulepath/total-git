using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class MergeLayoutTests
{
    private const string Text =
        "top\n" +
        "<<<<<<< HEAD\n" +
        "same 1\n" +
        "ours only\n" +
        "same 2\n" +
        "=======\n" +
        "same 1\n" +
        "same 2\n" +
        "theirs 1\n" +
        "theirs 2\n" +
        ">>>>>>> topic\n" +
        "bottom\n";

    private static (ConflictFile File, MergeDocument Doc) Load(string text = Text)
    {
        var file = ConflictFile.Parse(text);
        return (file, new MergeDocument(file));
    }

    [Fact]
    public void Aligns_matching_lines_of_ours_and_theirs_and_pads_the_rest()
    {
        var (_, doc) = Load();
        var layout = MergeLayout.Build(doc);

        // top, [same 1 | same 1], [ours only | -], [same 2 | same 2], [- | theirs 1], [- | theirs 2], bottom
        Assert.Equal(7, layout.Rows.Count);
        Assert.Equal((1, 5), layout.ConflictRows[0]);
        string? Ours(int row) => layout.Rows[row].Ours is var i and >= 0 ? layout.OursLines[i] : null;
        string? Theirs(int row) => layout.Rows[row].Theirs is var i and >= 0 ? layout.TheirsLines[i] : null;
        Assert.Equal(["top", "same 1", "ours only", "same 2", null, null, "bottom"], Enumerable.Range(0, 7).Select(Ours));
        Assert.Equal(["top", "same 1", null, "same 2", "theirs 1", "theirs 2", "bottom"], Enumerable.Range(0, 7).Select(Theirs));
        // Unresolved: the result has nothing in the block.
        Assert.All(Enumerable.Range(1, 5), r => Assert.Equal(-1, layout.Rows[r].Result));
        Assert.Equal(["top", "bottom"], layout.ResultLines.Select(l => l.Text));
    }

    [Fact]
    public void Result_lines_sit_level_with_where_they_came_from()
    {
        var (file, doc) = Load();
        doc.Resolutions[0].Set(file.Conflicts[0], MergeSide.Ours, MergeSide.Theirs);
        var layout = MergeLayout.Build(doc);

        // Ours' three lines on its rows (1..3), then theirs' after them, pushing the block to 7 rows.
        var block = layout.Rows.Skip(1).Take(layout.ConflictRows[0].RowCount).ToList();
        Assert.Equal(7, block.Count);
        Assert.Equal(
            ["same 1", "ours only", "same 2", "same 1", "same 2", "theirs 1", "theirs 2"],
            block.Select(r => layout.ResultLines[r.Result].Text));
        Assert.Equal(
            [ResultSource.Ours, ResultSource.Ours, ResultSource.Ours, ResultSource.Theirs, ResultSource.Theirs, ResultSource.Theirs, ResultSource.Theirs],
            block.Select(r => layout.ResultLines[r.Result].Source));
    }

    /// <summary>A table with rows added on each side and rows edited on both: as in an AGENTS.md conflict.</summary>
    private const string Table =
        "<<<<<<< HEAD\n" +
        "| `menu.ps1` | Interactive launcher menu. Entry point: `pwsh infra/local/menu.ps1` (or `pwsh workbench.ps1`) |\n" +
        "| `workbench.ps1` | Starts the Workbench |\n" +
        "| `workbench/` | The Insights Workbench: a local Blazor app |\n" +
        "| `scripts/run-insights.ps1` | Brings up the full dev stack in Windows Terminal panes |\n" +
        "| `scripts/docker-menu.ps1` | The local Docker stacks menu, start or rebuild the database |\n" +
        "| `scripts/wsl-startup-menu.ps1` | Controls automatic WSL/Docker startup at the current user |\n" +
        "| `scripts/Start-WslKeepAlive.ps1` | The watchdog the task runs: keeps the configured WSL distro up |\n" +
        "=======\n" +
        "| `menu.ps1` | Interactive launcher menu. Entry point: `pwsh infra/local/menu.ps1`. |\n" +
        "| `scripts/run-insights.ps1` | Brings up the full dev stack in Windows Terminal panes |\n" +
        "| `scripts/run-tests-docker.ps1` | Runs the test suites PR CI runs |\n" +
        "| `scripts/docker-menu.ps1` | The local Docker stacks menu, start or rebuild the database and seed it |\n" +
        "| `scripts/wsl-startup-menu.ps1` | Controls automatic WSL/Docker startup at the current user's logon |\n" +
        "| `scripts/Start-WslKeepAlive.ps1` | The watchdog invoked by the scheduled task above: keeps the configured WSL distro up |\n" +
        ">>>>>>> topic\n";

    [Fact]
    public void Lines_edited_on_one_side_sit_next_to_their_other_version_and_added_lines_get_rows_of_their_own()
    {
        var (_, doc) = Load(Table);
        var layout = MergeLayout.Build(doc);

        string? Key(IReadOnlyList<string> lines, int i) => i < 0 ? null : lines[i].Split('`')[1];
        var rows = layout.Rows.Select(r => (Key(layout.OursLines, r.Ours), Key(layout.TheirsLines, r.Theirs))).ToList();
        Assert.Equal(
        [
            ("menu.ps1", "menu.ps1"),
            ("workbench.ps1", null),
            ("workbench/", null),
            ("scripts/run-insights.ps1", "scripts/run-insights.ps1"),
            (null, "scripts/run-tests-docker.ps1"),
            ("scripts/docker-menu.ps1", "scripts/docker-menu.ps1"),
            ("scripts/wsl-startup-menu.ps1", "scripts/wsl-startup-menu.ps1"),
            ("scripts/Start-WslKeepAlive.ps1", "scripts/Start-WslKeepAlive.ps1"),
        ], rows);
        // Only the run-insights row is the same on both sides.
        Assert.Equal([3], Enumerable.Range(0, layout.Rows.Count).Where(layout.IsSameOnEverySide));
    }

    [Fact]
    public void Base_lines_line_up_with_the_lines_they_became()
    {
        var (_, doc) = Load(
            "<<<<<<< HEAD\n" +
            "ours added\n" +
            "alpha beta gamma delta\n" +
            "keep\n" +
            "||||||| base\n" +
            "alpha beta gamma\n" +
            "keep\n" +
            "removed by both\n" +
            "=======\n" +
            "alpha beta gamma epsilon\n" +
            "keep\n" +
            ">>>>>>> topic\n");
        var layout = MergeLayout.Build(doc);

        string? Line(IReadOnlyList<string> lines, int i) => i < 0 ? null : lines[i];
        var rows = layout.Rows.Select(r => (Line(layout.BaseLines, r.Base), Line(layout.OursLines, r.Ours), Line(layout.TheirsLines, r.Theirs))).ToList();
        Assert.Equal(
        [
            (null, "ours added", null),
            ("alpha beta gamma", "alpha beta gamma delta", "alpha beta gamma epsilon"),
            ("keep", "keep", "keep"),
            ("removed by both", null, null),
        ], rows);
    }

    [Theory]
    [InlineData("a b c d", "a b c d", 1.0)]
    [InlineData("a b c d", "a b c e", 0.75)]
    [InlineData("| `x` | y |", "| `z` | w |", 0.0)]
    public void Similarity_is_the_share_of_words_in_common(string a, string b, double expected)
    {
        Assert.Equal(expected, MergeLayout.Similarity(a, b), 3);
    }

    [Fact]
    public void An_empty_side_still_gets_a_row()
    {
        var (_, doc) = Load("a\n<<<<<<< HEAD\n=======\n>>>>>>> x\nb\n");
        var layout = MergeLayout.Build(doc);

        Assert.Equal(3, layout.Rows.Count);
        Assert.Equal(0, layout.Rows[1].Conflict);
        Assert.Equal(-1, layout.Rows[1].Ours);
    }

    [Fact]
    public void Hand_edited_shared_text_is_shown_in_the_result()
    {
        var (_, doc) = Load();
        doc.SetCommonEdit(0, ["top", "inserted"]);
        var layout = MergeLayout.Build(doc);

        Assert.Equal((0, 2), layout.SegmentRows[0]);
        Assert.Equal(-1, layout.Rows[1].Ours);
        Assert.Equal(new ResultLine("inserted", ResultSource.Custom), layout.ResultLines[layout.Rows[1].Result]);
        Assert.StartsWith("top\ninserted\n<<<<<<<", doc.Render());
    }
}

public sealed class ConflictResolutionTests
{
    private static readonly ConflictHunk Hunk = new(["o1", "o2"], ["b1"], ["t1", "t2", "t3"], "HEAD", "topic", "base");

    private static IEnumerable<string> Text(ConflictResolution r) => r.Lines(Hunk).Select(l => l.Text);

    [Fact]
    public void Sides_go_into_the_result_in_the_order_picked()
    {
        var r = new ConflictResolution();
        r.ToggleSide(MergeSide.Theirs, 3);
        r.ToggleSide(MergeSide.Ours, 2);

        Assert.Equal(["t1", "t2", "t3", "o1", "o2"], Text(r));
        Assert.Equal(1, r.OrderOf(MergeSide.Theirs));
        Assert.Equal(2, r.OrderOf(MergeSide.Ours));

        r.SwapOrder();
        Assert.Equal(["o1", "o2", "t1", "t2", "t3"], Text(r));

        r.ToggleSide(MergeSide.Ours, 2);
        Assert.Equal(["t1", "t2", "t3"], Text(r));
        r.ToggleSide(MergeSide.Theirs, 3);
        Assert.False(r.IsResolved);
    }

    [Fact]
    public void Single_lines_can_be_picked_and_dropped()
    {
        var r = new ConflictResolution();
        r.ToggleLine(MergeSide.Theirs, 1);
        r.ToggleLine(MergeSide.Ours, 0);
        Assert.Equal(["t2", "o1"], Text(r));

        r.ToggleSide(MergeSide.Theirs, 3); // partly picked → all of it
        Assert.Equal(["t1", "t2", "t3", "o1"], Text(r));
        r.ToggleLine(MergeSide.Theirs, 0);
        Assert.Equal(["t2", "t3", "o1"], Text(r));
        Assert.False(r.HasAll(MergeSide.Theirs, 3));

        r.ToggleLine(MergeSide.Ours, 0); // last line of a side drops the side
        Assert.Equal(0, r.OrderOf(MergeSide.Ours));
    }

    [Fact]
    public void Nothing_and_typed_text_are_resolutions_too()
    {
        var r = new ConflictResolution();
        r.UseNothing();
        Assert.True(r.IsResolved);
        Assert.Empty(Text(r));

        r.SetCustom(["mine"]);
        Assert.Equal(["mine"], Text(r));
        Assert.All(r.Lines(Hunk), l => Assert.Equal(ResultSource.Custom, l.Source));

        r.ToggleSide(MergeSide.Base, 1); // picking again drops the typed text
        Assert.Equal(["b1"], Text(r));
    }
}
