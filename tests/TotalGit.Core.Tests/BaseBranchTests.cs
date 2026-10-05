using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public class BaseBranchTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task Picks_the_main_line_the_branch_came_off()
    {
        // main: a; features/x: a <- f1 <- f2; topic: f2 <- t1. Topic came off features/x, not main.
        _repo.Commit("a");
        _repo.Git("switch", "-q", "-c", "features/x");
        _repo.Commit("f1");
        _repo.Commit("f2");
        _repo.Git("switch", "-q", "-c", "topic");
        _repo.Commit("t1");

        Assert.Equal("features/x", await BaseBranch.NearestAsync(_repo.Root, "topic", ["main", "features/x"], "main"));
    }

    [Fact]
    public async Task Skips_branches_that_already_contain_it_and_prefers_the_default_on_a_tie()
    {
        // topic is merged into features/y (contains it), and main and features/z sit at the same commit.
        _repo.Commit("a");
        _repo.Git("branch", "features/z");
        _repo.Git("switch", "-q", "-c", "topic");
        _repo.Commit("t1");
        _repo.Git("branch", "features/y");

        Assert.Equal("main", await BaseBranch.NearestAsync(_repo.Root, "topic", ["features/z", "features/y", "main"], "main"));
    }

    [Fact]
    public async Task Unknown_branch_has_no_base()
    {
        _repo.Commit("a");
        Assert.Null(await BaseBranch.NearestAsync(_repo.Root, "nope", ["main"]));
    }

    [Fact]
    public async Task Commit_list_has_full_shas_newest_first()
    {
        var first = _repo.Commit("one");
        var second = _repo.Commit("two");
        var third = _repo.Commit("three");

        var list = await GitActions.CommitListAsync(_repo.Root, $"{first}..{third}");

        Assert.Equal([third, second], list.Select(c => c.Sha));
        Assert.EndsWith(" three", list[0].Summary);
        Assert.Equal("Test User", list[0].Author);
    }
}
