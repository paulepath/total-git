using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class FastForwardBranchTests : IDisposable
{
    private readonly TestRepo _repo = new();
    private readonly TestRepo _remote = new(bare: true);
    private readonly TestRepo _other = new(path: Path.Combine(Path.GetTempPath(), "totalgit-tests", Guid.NewGuid().ToString("N")[..12] + "-other"));

    public void Dispose()
    {
        _repo.Dispose();
        _remote.Dispose();
        _other.Dispose();
    }

    /// <summary>A branch "topic" pushed from _repo, then moved on by two commits from another clone.</summary>
    private string BehindByTwo()
    {
        _repo.Commit("base");
        _repo.Git("remote", "add", "origin", _remote.Root);
        _repo.Git("branch", "topic");
        _repo.Git("push", "-q", "origin", "main", "topic");

        _other.Git("remote", "add", "origin", _remote.Root);
        _other.Git("fetch", "-q", "origin");
        _other.Git("switch", "-q", "-c", "topic", "origin/topic");
        _other.Commit("remote one", "r1.txt");
        var tip = _other.Commit("remote two", "r2.txt");
        _other.Git("push", "-q", "origin", "topic");
        return tip;
    }

    [Fact]
    public async Task Moves_a_branch_that_is_not_checked_out_to_the_remote_tip()
    {
        var tip = BehindByTwo();

        await GitActions.FastForwardBranchAsync(_repo.Root, "origin", "topic", "topic");

        Assert.Equal(tip, _repo.Git("rev-parse", "topic").Trim());
        Assert.Equal("main", _repo.Git("branch", "--show-current").Trim());
    }

    [Fact]
    public async Task Refuses_a_branch_with_its_own_commits()
    {
        BehindByTwo();
        _repo.Git("switch", "-q", "topic");
        _repo.Commit("local work", "l.txt");
        _repo.Git("switch", "-q", "main");

        await Assert.ThrowsAsync<GitCommandException>(() => GitActions.FastForwardBranchAsync(_repo.Root, "origin", "topic", "topic"));
    }

    [Fact]
    public async Task Pull_fast_forward_updates_the_current_branch()
    {
        var tip = BehindByTwo();
        _repo.Git("switch", "-q", "topic");
        _repo.Git("branch", "-q", "--set-upstream-to", "origin/topic");

        await GitActions.PullFastForwardAsync(_repo.Root);

        Assert.Equal(tip, _repo.Git("rev-parse", "HEAD").Trim());
    }
}
