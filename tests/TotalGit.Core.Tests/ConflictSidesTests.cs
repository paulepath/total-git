using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public class ConflictSidesTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    /// <summary>feature: one clean commit, then one that clashes with main's change to a.txt.</summary>
    private void SetUpClash()
    {
        _repo.Commit("base", "a.txt", "one");
        _repo.Git("switch", "-q", "-c", "feature");
        _repo.Commit("clean", "b.txt", "b");
        _repo.Commit("feature edit", "a.txt", "two");
        _repo.Git("switch", "-q", "main");
        _repo.Commit("main edit", "a.txt", "three");
    }

    [Fact]
    public async Task A_rebase_names_the_branch_it_moves_onto_and_where_the_commit_comes_from()
    {
        SetUpClash();
        _repo.Git("switch", "-q", "feature");
        Assert.Equal(OperationOutcome.Stopped, await GitActions.RebaseAsync(_repo.Root, "main"));

        var sides = await ConflictSides.DescribeAsync(_repo.Root);

        Assert.NotNull(sides);
        Assert.Equal("main", sides.Ours);
        Assert.Equal("what you're rebasing onto + 1 of your commits already moved", sides.OursRole);
        Assert.Null(sides.Theirs); // git's "<sha> (feature edit)" already says which commit
        Assert.Equal("your commit, moving from feature", sides.TheirsRole);
    }

    [Fact]
    public async Task A_merge_names_the_current_branch()
    {
        SetUpClash();
        Assert.Equal(OperationOutcome.Stopped, await GitActions.MergeAsync(_repo.Root, "feature"));

        var sides = await ConflictSides.DescribeAsync(_repo.Root);

        Assert.Equal("main", sides?.Ours);
        Assert.Equal("the branch being merged in", sides?.TheirsRole);
    }

    [Fact]
    public async Task Nothing_in_progress_keeps_gits_labels()
    {
        _repo.Commit("base", "a.txt", "one");
        Assert.Null(await ConflictSides.DescribeAsync(_repo.Root));
    }
}
