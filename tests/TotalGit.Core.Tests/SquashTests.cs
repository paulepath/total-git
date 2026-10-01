using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class SquashTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    private string Log() => _repo.Git("log", "--format=%s").Replace("\r", "");

    private string Tree() => _repo.Git("rev-parse", "HEAD^{tree}");

    [Fact]
    public async Task Squashes_the_tip_commits_with_a_new_message()
    {
        _repo.Commit("base", "base.txt", "0");
        var a = _repo.Commit("A", "a.txt", "a");
        var b = _repo.Commit("B", "b.txt", "b");
        var c = _repo.Commit("C", "c.txt", "c");
        var tree = Tree();

        var outcome = await GitActions.SquashAsync(_repo.Root, [a, b, c], "Add a, b and c\n\nIn one go.");

        Assert.Equal(OperationOutcome.Completed, outcome);
        Assert.Equal("Add a, b and c\nbase", Log());
        Assert.Equal("Add a, b and c\n\nIn one go.", _repo.Git("log", "-1", "--format=%B").Replace("\r", "").Trim());
        Assert.Equal(tree, Tree());
    }

    [Fact]
    public async Task Squashes_commits_below_the_tip_and_replays_the_rest()
    {
        _repo.Commit("base", "base.txt", "0");
        var a = _repo.Commit("A", "a.txt", "a");
        var b = _repo.Commit("B", "b.txt", "b");
        _repo.Commit("C", "c.txt", "c");
        var tree = Tree();

        await GitActions.SquashAsync(_repo.Root, [a, b], "A and B");

        Assert.Equal("C\nA and B\nbase", Log());
        Assert.Equal(tree, Tree());
    }

    [Fact]
    public async Task Squashes_from_the_root_commit()
    {
        var a = _repo.Commit("A", "a.txt", "a");
        var b = _repo.Commit("B", "b.txt", "b");

        await GitActions.SquashAsync(_repo.Root, [a, b], "Start");

        Assert.Equal("Start", Log());
    }

    [Fact]
    public async Task Commits_off_the_checked_out_branch_are_refused()
    {
        _repo.Commit("base", "base.txt", "0");
        _repo.Git("switch", "-q", "-c", "topic");
        var a = _repo.Commit("A", "a.txt", "a");
        var b = _repo.Commit("B", "b.txt", "b");
        _repo.Git("switch", "-q", "main");

        Assert.Contains("Check out their branch", await GitActions.SquashProblemAsync(_repo.Root, [a, b]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitActions.SquashAsync(_repo.Root, [a, b], "x"));
    }

    [Fact]
    public async Task A_merge_after_the_commits_is_refused()
    {
        _repo.Commit("base", "base.txt", "0");
        var a = _repo.Commit("A", "a.txt", "a");
        var b = _repo.Commit("B", "b.txt", "b");
        _repo.Git("switch", "-q", "-c", "side");
        _repo.Commit("S", "s.txt", "s");
        _repo.Git("switch", "-q", "main");
        _repo.Commit("C", "c.txt", "c");
        _repo.Git("merge", "--no-ff", "-q", "-m", "Merge side", "side");

        Assert.Contains("merge commits after", await GitActions.SquashProblemAsync(_repo.Root, [a, b]));
    }
}
