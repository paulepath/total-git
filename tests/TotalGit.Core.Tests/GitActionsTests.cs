using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class GitActionsTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task Stage_unstage_and_commit_round_trip()
    {
        _repo.Commit("base", "a.txt", "a");
        _repo.Write("a.txt", "changed");
        _repo.Write("b.txt", "new");

        await GitActions.StageAsync(_repo.Root, ["a.txt", "b.txt"]);
        Assert.Equal(2, _repo.Git("diff", "--cached", "--name-only").Split('\n').Length);

        await GitActions.UnstageAsync(_repo.Root, ["b.txt"]);
        Assert.Equal("a.txt", _repo.Git("diff", "--cached", "--name-only"));

        await GitActions.CommitAsync(_repo.Root, "Summary line\n\nDescription");
        Assert.Equal("Summary line", _repo.Git("log", "-1", "--format=%s"));
        Assert.Equal("Description", _repo.Git("log", "-1", "--format=%b"));
        Assert.Equal("?? b.txt", _repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Creates_lightweight_and_annotated_tags()
    {
        var first = _repo.Commit("one");
        _repo.Commit("two");

        await GitActions.CreateTagAsync(_repo.Root, "v1", first);
        await GitActions.CreateTagAsync(_repo.Root, "v2", "HEAD", "Release two\n\nNotes");

        Assert.Equal("commit", _repo.Git("cat-file", "-t", "v1"));
        Assert.Equal(first, _repo.Git("rev-parse", "v1"));
        Assert.Equal("tag", _repo.Git("cat-file", "-t", "v2"));
        Assert.Equal("Release two", _repo.Git("tag", "-l", "--format=%(contents:subject)", "v2"));

        await GitActions.DeleteTagAsync(_repo.Root, "v1");
        Assert.Equal("v2", _repo.Git("tag", "-l"));
    }

    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("release/2026-09", true)]
    [InlineData("has space", false)]
    [InlineData("a..b", false)]
    [InlineData("-x", false)]
    [InlineData("x.lock", false)]
    [InlineData("x/.hidden", false)]
    [InlineData("x~1", false)]
    [InlineData("", false)]
    public void Validates_ref_names(string name, bool valid) => Assert.Equal(valid, GitActions.IsValidRefName(name));

    [Fact]
    public async Task Stage_and_unstage_all_before_first_commit()
    {
        _repo.Write("a.txt", "a");

        await GitActions.StageAllAsync(_repo.Root);
        Assert.Equal("A  a.txt", _repo.Git("status", "--porcelain"));

        await GitActions.UnstageAllAsync(_repo.Root);
        Assert.Equal("?? a.txt", _repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Stages_deleted_files()
    {
        _repo.Commit("base", "a.txt", "a");
        File.Delete(Path.Combine(_repo.Root, "a.txt"));

        await GitActions.StageAsync(_repo.Root, ["a.txt"]);

        Assert.Equal("D  a.txt", _repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Checkout_local_branch()
    {
        _repo.Commit("base");
        _repo.Git("branch", "other");

        await GitActions.CheckoutAsync(_repo.Root, "other");

        Assert.Equal("other", _repo.Git("branch", "--show-current"));
    }

    [Fact]
    public async Task Checkout_branch_used_by_another_worktree_reports_its_path()
    {
        _repo.Commit("base");
        var wt = Path.Combine(_repo.Root, ".worktrees", "other");
        _repo.Git("worktree", "add", "-q", "-b", "other", wt);

        var ex = await Assert.ThrowsAsync<BranchInUseException>(() => GitActions.CheckoutAsync(_repo.Root, "other"));

        Assert.Equal(Path.GetFullPath(wt), Path.GetFullPath(ex.WorktreePath), ignoreCase: true);
    }

    [Fact]
    public async Task Push_sets_upstream_then_pull_and_remote_checkout_work()
    {
        using var remote = new TestRepo(bare: true);
        _repo.Commit("base");
        _repo.Git("remote", "add", "origin", remote.Root);

        await GitActions.PushAsync(_repo.Root);
        Assert.Equal("origin/main", _repo.Git("rev-parse", "--abbrev-ref", "main@{u}"));

        // A second clone pushes a feature branch and a new main commit.
        using var other = new TestRepo(path: Path.Combine(Path.GetTempPath(), "totalgit-tests", Guid.NewGuid().ToString("N")[..12] + "-clone"));
        other.Git("pull", "-q", remote.Root, "main");
        other.Git("remote", "add", "origin", remote.Root);
        other.Commit("from other");
        other.Git("push", "-q", "origin", "main");
        other.Git("push", "-q", "origin", "main:feature/x");

        await GitActions.FetchAsync(_repo.Root);
        await GitActions.PullAsync(_repo.Root);
        Assert.Equal("from other", _repo.Git("log", "-1", "--format=%s"));

        var branch = await GitActions.CheckoutRemoteAsync(_repo.Root, "origin", "feature/x");
        Assert.Equal("feature/x", branch);
        Assert.Equal("feature/x", _repo.Git("branch", "--show-current"));
        Assert.Equal("origin/feature/x", _repo.Git("rev-parse", "--abbrev-ref", "feature/x@{u}"));

        _repo.Commit("on feature");
        await GitActions.PushAsync(_repo.Root);
        Assert.Equal(_repo.Git("rev-parse", "HEAD"), TestRepo.RunGit(remote.Root, "rev-parse", "feature/x"));
    }
}
