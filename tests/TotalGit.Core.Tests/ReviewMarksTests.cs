using TotalGit.Core.Git;
using TotalGit.Core.Hosting;

namespace TotalGit.Core.Tests;

public sealed class ReviewMarksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "totalgit-marks-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static readonly RemoteHostInfo Repo = new(HostKind.GitHub, "github.com", "Octo", "Widgets", "origin");

    [Fact]
    public void Marks_are_kept_per_pull_request_and_survive_a_reload()
    {
        var file = Path.Combine(_dir, "review-marks.json");
        var pr7 = ReviewMarks.Key(Repo, 7);
        var marks = new ReviewMarks(file);
        var at = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

        marks.Mark(pr7, "src/a.py", new ReviewMark("head1", "blob1", at));
        marks.Mark(ReviewMarks.Key(Repo, 8), "src/a.py", new ReviewMark("other", null, at));
        marks.Mark(pr7, "src/b.py", new ReviewMark("head1", "blob2", at));
        marks.Unmark(pr7, "src/b.py");

        var reloaded = new ReviewMarks(file);
        Assert.Equal("github.com/octo/widgets#7", pr7);
        Assert.Equal(new ReviewMark("head1", "blob1", at), reloaded.Get(pr7, "src/a.py"));
        Assert.Null(reloaded.Get(pr7, "src/b.py"));
        Assert.Single(reloaded.All(pr7));
        Assert.Equal("other", reloaded.Get(ReviewMarks.Key(Repo, 8), "src/a.py")!.HeadSha);
    }

    [Fact]
    public void A_damaged_file_is_treated_as_no_marks()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "review-marks.json");
        File.WriteAllText(file, "{ not json");

        Assert.Empty(new ReviewMarks(file).All("x"));
    }

    [Theory]
    [InlineData(FileViewState.Unviewed, null, "b1", FileReviewState.NotReviewed)]
    [InlineData(FileViewState.Viewed, null, "b1", FileReviewState.Reviewed)]
    [InlineData(FileViewState.Viewed, "b1", "b1", FileReviewState.Reviewed)]
    [InlineData(FileViewState.Viewed, "b0", "b1", FileReviewState.ChangedSinceReview)]
    [InlineData(FileViewState.ChangedSinceViewed, null, "b1", FileReviewState.ChangedSinceReview)]
    [InlineData(FileViewState.Unviewed, "b1", "b1", FileReviewState.Reviewed)]
    [InlineData(FileViewState.Unviewed, "b0", "b1", FileReviewState.ChangedSinceReview)]
    public void Review_state_combines_the_hosts_mark_with_the_local_one(FileViewState host, string? markedBlob, string current, FileReviewState expected)
    {
        var local = markedBlob is null ? null : new ReviewMark("h", markedBlob, DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, ReviewMarks.StateOf(host, local, current));
    }

    [Fact]
    public void Delta_lists_lines_added_since_the_review_and_where_lines_went()
    {
        FileDiff diff = new("a.py", false,
        [
            new(DiffLineKind.Hunk, null, null, "@@ -3,4 +3,5 @@"),
            new(DiffLineKind.Context, 3, 3, "keep"),
            new(DiffLineKind.Removed, 4, null, "old"),
            new(DiffLineKind.Added, null, 4, "new"),
            new(DiffLineKind.Added, null, 5, "more"),
            new(DiffLineKind.Context, 5, 6, "keep"),
            new(DiffLineKind.Hunk, null, null, "@@ -20,3 +21,2 @@"),
            new(DiffLineKind.Context, 20, 21, "keep"),
            new(DiffLineKind.Removed, 21, null, "gone"),
            new(DiffLineKind.Context, 22, 22, "keep"),
        ], false);

        var delta = ReviewDelta.From(diff);

        Assert.Equal([4, 5], delta.NewLines.Order());
        Assert.Equal([4, 22], delta.RemovedBefore.Order());
        Assert.Equal(3, delta.Count);
    }

    [Theory]
    [InlineData("package-lock.json", true)]
    [InlineData("web/yarn.lock", true)]
    [InlineData("src/Forms/Main.Designer.cs", true)]
    [InlineData("obj/App.g.cs", true)]
    [InlineData("site/app.min.js", true)]
    [InlineData("src/__snapshots__/card.test.js.snap", true)]
    [InlineData("packages/ui/dist/index.js", true)]
    [InlineData("src/main.ts", false)]
    [InlineData("docs/locking.md", false)]
    public void Generated_files_are_recognised(string path, bool generated)
    {
        Assert.Equal(generated, GeneratedFiles.IsGenerated(path));
    }

    [Theory]
    [InlineData("tests/TotalGit.Core.Tests/ReviewMarksTests.cs", true)]
    [InlineData("src/App.UnitTests/Thing.cs", true)]
    [InlineData("src/Widgets/WidgetTests.cs", true)]
    [InlineData("src/Widgets/WidgetTest.java", true)]
    [InlineData("web/src/card.test.tsx", true)]
    [InlineData("web/src/card.spec.js", true)]
    [InlineData("web/src/__tests__/card.js", true)]
    [InlineData("pkg/paging_test.go", true)]
    [InlineData("app/test_paging.py", true)]
    [InlineData("e2e/login.ts", true)]
    [InlineData("src/Widgets/Widget.cs", false)]
    [InlineData("src/latest.ts", false)]
    [InlineData("src/Contest.cs", false)]
    [InlineData("docs/testing-guide.md", false)]
    [InlineData("src/Testimonials/Quote.cs", false)]
    public void Test_files_are_recognised(string path, bool test)
    {
        Assert.Equal(test, TestFiles.IsTest(path));
    }

    [Fact]
    public async Task Gitattributes_can_mark_files_generated()
    {
        using var repo = new TestRepo();
        repo.Write(".gitattributes", "api/schema.ts linguist-generated=true\n*.pb.go linguist-generated\n");
        repo.Commit("attrs", ".gitattributes", "api/schema.ts linguist-generated=true\n*.pb.go linguist-generated\n");

        var marked = await GitActions.GeneratedAttrAsync(repo.Root, ["api/schema.ts", "x/y.pb.go", "src/main.ts"]);

        Assert.Equal(["api/schema.ts", "x/y.pb.go"], marked.Order());
        Assert.True(GeneratedFiles.IsGenerated("api/schema.ts", marked));
    }
}
