using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public class GraphRefTests
{
    [Fact]
    public async Task A_commit_on_a_branch_is_on_the_graph()
    {
        using var repo = new TestRepo();
        var first = repo.Commit("first");
        repo.Commit("second");
        Assert.True(await GitActions.IsOnGraphRefAsync(repo.Root, first));
    }

    [Fact]
    public async Task A_commit_that_isnt_here_is_not()
    {
        using var repo = new TestRepo();
        repo.Commit("first");
        Assert.False(await GitActions.IsOnGraphRefAsync(repo.Root, new string('a', 40)));
    }

    [Fact]
    public async Task A_commit_only_a_pull_request_ref_reaches_is_not()
    {
        using var repo = new TestRepo();
        repo.Commit("first");
        repo.Git("switch", "-q", "-c", "fork");
        var head = repo.Commit("from a fork");
        repo.Git("update-ref", "refs/totalgit/pr/1", head);
        repo.Git("switch", "-q", "main");
        repo.Git("branch", "-D", "fork");

        Assert.False(await GitActions.IsOnGraphRefAsync(repo.Root, head));
    }

    [Fact]
    public async Task A_detached_head_counts()
    {
        using var repo = new TestRepo();
        repo.Commit("first");
        repo.Git("switch", "-q", "--detach");
        var head = repo.Commit("detached");
        Assert.True(await GitActions.IsOnGraphRefAsync(repo.Root, head));
    }
}
