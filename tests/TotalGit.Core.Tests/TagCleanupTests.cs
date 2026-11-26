using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class TagCleanupTests : IDisposable
{
    private readonly TestRepo _repo = new();
    private readonly TestRepo _remote = new(bare: true);

    public TagCleanupTests()
    {
        _repo.Commit("base");
        _repo.Git("remote", "add", "origin", _remote.Root);
        _repo.Git("push", "-q", "origin", "main");
    }

    public void Dispose()
    {
        _repo.Dispose();
        _remote.Dispose();
    }

    [Fact]
    public async Task Lists_the_local_tags_the_remote_does_not_have()
    {
        var sha = _repo.Git("rev-parse", "HEAD");
        _repo.Git("tag", "pushed");
        _repo.Git("tag", "-a", "pushed-annotated", "-m", "release one");
        _repo.Git("push", "-q", "origin", "pushed", "pushed-annotated");
        _repo.Git("tag", "never-pushed");
        _repo.Git("tag", "-a", "local-annotated", "-m", "release two");
        // Deleted on the remote after it was pushed: it looks the same as one never pushed.
        _repo.Git("tag", "deleted-there");
        _repo.Git("push", "-q", "origin", "deleted-there");
        _repo.Git("push", "-q", "origin", ":refs/tags/deleted-there");

        var tags = await GitActions.TagsNotOnRemoteAsync(_repo.Root, "origin");

        Assert.Equal(["deleted-there", "local-annotated", "never-pushed"], tags.Select(t => t.Name).Order());
        Assert.All(tags, t => Assert.Equal(sha, t.Sha));
        Assert.All(tags, t => Assert.False(t.OnNoBranch));
        var annotated = tags.Single(t => t.Name == "local-annotated");
        Assert.True(annotated.IsAnnotated);
        Assert.Equal("release two", annotated.Subject);
        Assert.Equal("base", tags.Single(t => t.Name == "never-pushed").Subject);
    }

    [Fact]
    public async Task Says_when_a_tag_is_all_that_holds_its_commit()
    {
        _repo.Git("switch", "-q", "-c", "experiment");
        var loose = _repo.Commit("an experiment", "experiment.txt");
        _repo.Git("tag", "kept-experiment");
        _repo.Git("switch", "-q", "main");
        _repo.Git("branch", "-q", "-D", "experiment");
        _repo.Git("tag", "on-main");

        var tags = await GitActions.TagsNotOnRemoteAsync(_repo.Root, "origin");

        Assert.True(tags.Single(t => t.Name == "kept-experiment").OnNoBranch);
        Assert.Equal(loose, tags.Single(t => t.Name == "kept-experiment").Sha);
        Assert.False(tags.Single(t => t.Name == "on-main").OnNoBranch);
    }

    [Fact]
    public async Task Throws_when_the_remote_cannot_be_reached()
    {
        _repo.Git("tag", "v1");
        _repo.Git("remote", "set-url", "origin", Path.Combine(_remote.Root, "no-such-repository"));

        await Assert.ThrowsAsync<GitCommandException>(() => GitActions.TagsNotOnRemoteAsync(_repo.Root, "origin"));
    }

    [Fact]
    public async Task Deletes_the_tags_it_can_and_reports_the_rest()
    {
        _repo.Git("tag", "one");
        _repo.Git("tag", "two");

        Assert.Null(await GitActions.DeleteTagsAsync(_repo.Root, ["one"]));
        var error = await GitActions.DeleteTagsAsync(_repo.Root, ["no-such-tag", "two"]);

        Assert.Contains("no-such-tag", error);
        Assert.Equal("", _repo.Git("tag", "--list"));
    }
}
