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
    public async Task Rev_list_gives_a_branchs_own_commits_including_a_merge_of_its_base()
    {
        using var repo = new TestRepo();
        repo.Commit("base", "a.txt", "one");
        repo.Git("switch", "-q", "-c", "feature");
        var first = repo.Commit("feature one", "b.txt", "b");
        repo.Git("switch", "-q", "main");
        repo.Commit("main moves on", "c.txt", "c");
        repo.Git("switch", "-q", "feature");
        repo.Git("merge", "-q", "--no-edit", "main");
        var merge = repo.Git("rev-parse", "HEAD");
        var last = repo.Commit("feature two", "b.txt", "bb");

        Assert.Equal([last, merge, first], await GitActions.RevListAsync(repo.Root, "main..feature", 500));
        Assert.Equal([last], await GitActions.RevListAsync(repo.Root, "main..feature", 1));
        Assert.Empty(await GitActions.RevListAsync(repo.Root, "no-such-branch..feature", 10));
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
