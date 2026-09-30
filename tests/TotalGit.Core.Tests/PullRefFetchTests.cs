using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class PullRefFetchTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task Fetches_a_pull_request_ref_that_no_branch_contains()
    {
        using var remote = new TestRepo(bare: true);
        _repo.Commit("base");
        _repo.Git("remote", "add", "origin", remote.Root);
        _repo.Git("push", "-q", "origin", "main");

        // The pull request's commit lives only under refs/pull on the server, as on GitHub.
        _repo.Git("switch", "-q", "-c", "contributor");
        var prHead = _repo.Commit("contribution", "pr.txt");
        _repo.Git("push", "-q", "origin", "contributor:refs/pull/1/head");
        _repo.Git("switch", "-q", "main");
        _repo.Git("branch", "-q", "-D", "contributor");
        remote.Git("update-ref", "refs/pull/1/head", prHead);

        using var clone = new TestRepo(path: Path.Combine(Path.GetTempPath(), "totalgit-tests", Guid.NewGuid().ToString("N")[..12] + "-clone"));
        clone.Git("remote", "add", "origin", remote.Root);
        clone.Git("fetch", "-q", "origin");
        Assert.Throws<InvalidOperationException>(() => clone.Git("cat-file", "-e", prHead));

        await GitActions.FetchRefsAsync(clone.Root, "origin", "+refs/pull/1/head:refs/totalgit/pr/1", "+refs/heads/main:refs/remotes/origin/main");
        Assert.Equal(prHead, clone.Git("rev-parse", "refs/totalgit/pr/1"));

        using var session = RepositorySession.Open(clone.Root);
        Assert.Equal(prHead, session.ResolveCommit("refs/totalgit/pr/1"));
        Assert.Null(session.ResolveCommit("refs/totalgit/pr/2"));
        Assert.Equal(clone.Git("rev-parse", "origin/main"), session.MergeBase("origin/main", "refs/totalgit/pr/1"));
    }

    [Fact]
    public void Resolve_commit_peels_annotated_tags()
    {
        var sha = _repo.Commit("tagged");
        _repo.Git("tag", "-a", "v1", "-m", "release");

        using var session = RepositorySession.Open(_repo.Root);
        Assert.Equal(sha, session.ResolveCommit("v1"));
        Assert.Equal(sha, session.ResolveCommit("refs/tags/v1"));
        Assert.Equal(sha, session.ResolveCommit("main"));
        Assert.Null(session.ResolveCommit("no-such-ref"));
    }

    [Fact]
    public async Task Fetching_a_missing_ref_throws()
    {
        using var remote = new TestRepo(bare: true);
        _repo.Commit("base");
        _repo.Git("remote", "add", "origin", remote.Root);
        _repo.Git("push", "-q", "origin", "main");

        await Assert.ThrowsAsync<GitCommandException>(
            () => GitActions.FetchRefsAsync(_repo.Root, "origin", "+refs/pull/99/head:refs/totalgit/pr/99"));
    }
}
