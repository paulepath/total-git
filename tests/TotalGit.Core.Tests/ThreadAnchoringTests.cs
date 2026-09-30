using TotalGit.Core.Git;
using TotalGit.Core.Hosting;

namespace TotalGit.Core.Tests;

public class ThreadAnchoringTests
{
    // @@ -1,4 +1,4 @@
    //  1 1  a
    //  2    -b
    //    2  +B
    //  3 3  c
    //  4    -d
    private static readonly DiffLine[] Lines =
    [
        new(DiffLineKind.Hunk, null, null, "@@ -1,4 +1,4 @@"),
        new(DiffLineKind.Context, 1, 1, "a"),
        new(DiffLineKind.Removed, 2, null, "b"),
        new(DiffLineKind.Added, null, 2, "B"),
        new(DiffLineKind.Context, 3, 3, "c"),
        new(DiffLineKind.Removed, 4, null, "d"),
    ];

    private static ReviewThread Thread(string id, DiffSide side, int? line, string path = "f.txt", bool outdated = false) =>
        new(id, new CommentAnchor(path, side, line), false, outdated, [], true, true);

    [Fact]
    public void Places_threads_on_the_side_they_were_made_on()
    {
        var placement = ThreadAnchoring.Place(Lines,
        [
            Thread("new2", DiffSide.Right, 2),
            Thread("old2", DiffSide.Left, 2),
            Thread("ctx", DiffSide.Right, 3),
            Thread("oldctx", DiffSide.Left, 3),
            Thread("old4", DiffSide.Left, 4),
        ], "f.txt");

        Assert.Equal("new2", Assert.Single(placement.ByLine[3]).Id);
        Assert.Equal("old2", Assert.Single(placement.ByLine[2]).Id);
        Assert.Equal(["ctx", "oldctx"], placement.ByLine[4].Select(t => t.Id));
        Assert.Equal("old4", Assert.Single(placement.ByLine[5]).Id);
        Assert.Empty(placement.Unplaced);
    }

    [Fact]
    public void Outdated_and_out_of_range_threads_are_unplaced_and_other_files_ignored()
    {
        var placement = ThreadAnchoring.Place(Lines,
        [
            Thread("outdated", DiffSide.Right, null, outdated: true),
            Thread("far", DiffSide.Right, 40),
            Thread("left-added", DiffSide.Left, 99),
            Thread("other", DiffSide.Right, 1, path: "g.txt"),
        ], "f.txt");

        Assert.Empty(placement.ByLine);
        Assert.Equal(["outdated", "far", "left-added"], placement.Unplaced.Select(t => t.Id));
    }
}
