using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public class AmendTests
{
    [Fact]
    public async Task Amend_preserves_parent_and_author_and_includes_only_staged_changes()
    {
        using var repo = new TestRepo();
        var parent = repo.Commit("first", "a.txt", "one");
        repo.Commit("subject\n\nbody", "b.txt", "two");
        var author = repo.Git("show", "-s", "--format=%an <%ae> %at", "HEAD");
        Assert.Equal("subject\n\nbody", (await GitActions.HeadMessageAsync(repo.Root))!.Replace("\r\n", "\n"));
        repo.Write("a.txt", "staged");
        repo.Git("add", "a.txt");
        repo.Write("b.txt", "unstaged");
        await GitActions.AmendAsync(repo.Root, "replacement\n\nnew body");
        Assert.Equal(parent, repo.Git("rev-parse", "HEAD^"));
        Assert.Equal(author, repo.Git("show", "-s", "--format=%an <%ae> %at", "HEAD"));
        Assert.Equal("staged", repo.Git("show", "HEAD:a.txt"));
        Assert.Equal("two", repo.Git("show", "HEAD:b.txt"));
        Assert.Equal("replacement\n\nnew body", (await GitActions.HeadMessageAsync(repo.Root))!.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Message_only_amend_and_pushed_detection()
    {
        using var repo = new TestRepo();
        var sha = repo.Commit("old");
        Assert.False(await GitActions.HeadIsPushedAsync(repo.Root));
        repo.Git("update-ref", "refs/remotes/origin/main", sha);
        Assert.True(await GitActions.HeadIsPushedAsync(repo.Root));
        await GitActions.AmendAsync(repo.Root, "new");
        Assert.NotEqual(sha, repo.Git("rev-parse", "HEAD"));
        Assert.False(await GitActions.HeadIsPushedAsync(repo.Root));
        Assert.Equal("1", repo.Git("rev-list", "--count", "HEAD"));
    }

    [Theory]
    [InlineData("MERGE_HEAD")]
    [InlineData("rebase-merge")]
    public async Task Unborn_head_and_operation_in_progress_are_rejected(string operation)
    {
        using var repo = new TestRepo();
        Assert.Null(await GitActions.HeadMessageAsync(repo.Root));
        Assert.False(await GitActions.HeadIsPushedAsync(repo.Root));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitActions.AmendAsync(repo.Root, "no"));
        var sha = repo.Commit("first");
        var path = Path.Combine(repo.Root, ".git", operation);
        if (operation == "MERGE_HEAD") File.WriteAllText(path, sha);
        else Directory.CreateDirectory(path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitActions.AmendAsync(repo.Root, "no"));
        Assert.Equal(sha, repo.Git("rev-parse", "HEAD"));
    }
}
