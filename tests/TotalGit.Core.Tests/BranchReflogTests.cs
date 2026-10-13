using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class BranchReflogTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public BranchReflogTests() => _repo.Commit("base");

    public void Dispose() => _repo.Dispose();

    private RefInfo Branch(string name, string? folder = null)
    {
        using var session = RepositorySession.Open(folder ?? _repo.Root);
        return session.LoadState().Refs.Single(r => r.Kind == RefKind.LocalBranch && r.Name == name);
    }

    [Fact]
    public void A_branch_just_made_from_another_is_new_and_says_where_from()
    {
        _repo.Git("branch", "feature/x", "main");

        var branch = Branch("feature/x");
        Assert.True(branch.IsNew);
        Assert.Equal("main", branch.CreatedFrom);
    }

    [Fact]
    public void A_branch_made_and_checked_out_from_a_sha_is_new()
    {
        var sha = _repo.Git("rev-parse", "HEAD");
        _repo.Git("switch", "-q", "-c", "topic", sha);

        // Made from a SHA (as the app does): named after the branch at that commit.
        var branch = Branch("topic");
        Assert.True(branch.IsNew);
        Assert.Equal("main", branch.CreatedFrom);
    }

    [Fact]
    public void A_branch_made_from_an_older_commit_names_the_branch_holding_it()
    {
        var old = _repo.Git("rev-parse", "HEAD");
        _repo.Commit("newer on main");
        _repo.Git("branch", "topic", old);

        var branch = Branch("topic");
        Assert.True(branch.IsNew);
        Assert.Equal("main", branch.CreatedFrom);
    }

    [Fact]
    public void A_branch_checked_out_from_someone_elses_remote_branch_is_not_new()
    {
        // Their commit is only on origin/theirs; checking it out makes a local branch at it.
        _repo.Git("remote", "add", "origin", _repo.Root);
        _repo.Git("switch", "-q", "-c", "scratch");
        _repo.Commit("their work");
        _repo.Git("update-ref", "refs/remotes/origin/theirs", "HEAD");
        _repo.Git("switch", "-q", "main");
        _repo.Git("branch", "-D", "scratch");
        _repo.Git("branch", "theirs", "origin/theirs");

        var branch = Branch("theirs");
        Assert.False(branch.IsNew);
        Assert.Null(branch.CreatedFrom);
    }

    [Fact]
    public void A_branch_made_from_a_remote_main_is_new()
    {
        _repo.Git("remote", "add", "origin", _repo.Root);
        _repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");
        _repo.Commit("local only");
        _repo.Git("branch", "hot-fix/x", "origin/main");

        var branch = Branch("hot-fix/x");
        Assert.True(branch.IsNew);
        Assert.Equal("origin/main", branch.CreatedFrom);
    }

    [Fact]
    public void A_branch_with_a_commit_or_moved_since_is_not_new()
    {
        _repo.Git("switch", "-q", "-c", "topic");
        _repo.Commit("on topic");
        _repo.Git("branch", "moved", "main");
        _repo.Git("branch", "-f", "moved", "topic");

        Assert.False(Branch("topic").IsNew);
        Assert.False(Branch("moved").IsNew);
    }

    [Fact]
    public void The_branch_the_repository_started_on_is_not_new()
    {
        var main = Branch("main");
        Assert.False(main.IsNew);
        Assert.Null(main.CreatedFrom);
    }

    [Fact]
    public void A_branch_without_a_reflog_is_not_new()
    {
        _repo.Git("update-ref", "refs/heads/fetched", "HEAD");
        File.Delete(Path.Combine(_repo.Root, ".git", "logs", "refs", "heads", "fetched"));

        Assert.False(Branch("fetched").IsNew);
    }

    [Fact]
    public void Read_from_a_linked_worktree_too()
    {
        var folder = Path.Combine(Path.GetTempPath(), "totalgit-tests", Guid.NewGuid().ToString("N")[..12] + "-wt");
        _repo.Git("worktree", "add", "-q", "-b", "in-worktree", folder);
        try
        {
            var branch = Branch("in-worktree", folder);
            Assert.True(branch.IsNew);
            Assert.True(branch.IsCurrent);
        }
        finally
        {
            _repo.Git("worktree", "remove", "--force", folder);
        }
    }
}
