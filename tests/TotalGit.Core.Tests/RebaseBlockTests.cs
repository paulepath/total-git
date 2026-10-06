using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public class RebaseBlockTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    /// <summary>
    /// feature adds cfg/stack.yaml, later stops tracking it (ignored, the local copy kept); main moves on. Rebasing
    /// feature onto main then finds the local copy untracked when it replays the commit that added it.
    /// </summary>
    private string SetUpStuckRebase()
    {
        _repo.Commit("base", "readme.md", "base");
        _repo.Git("switch", "-q", "-c", "feature");
        var adds = _repo.Commit("Add the stack", "cfg/stack.yaml", "committed");
        _repo.Commit("Other work", "app.txt", "work");
        _repo.Git("rm", "-q", "--cached", "cfg/stack.yaml");
        _repo.Write(".gitignore", "cfg/stack.yaml\n");
        _repo.Git("add", ".gitignore");
        _repo.Git("commit", "-q", "-m", "Stop tracking the stack");
        _repo.Write("cfg/stack.yaml", "local secret");
        _repo.Git("switch", "-q", "main");
        // Not Commit(): its add -A would commit the local stack file (main doesn't ignore it).
        _repo.Write("main.txt", "m");
        _repo.Git("add", "main.txt");
        _repo.Git("commit", "-q", "-m", "main moves on");
        _repo.Git("switch", "-q", "feature");
        Assert.Equal("local secret", File.ReadAllText(Path.Combine(_repo.Root, "cfg/stack.yaml")));
        return adds;
    }

    [Fact]
    public async Task Finds_the_step_git_keeps_failing_and_the_files_in_its_way()
    {
        var adds = SetUpStuckRebase();

        Assert.Equal(OperationOutcome.Stopped, await GitActions.RebaseAsync(_repo.Root, "main"));
        var blocked = await RebaseBlock.FindAsync(_repo.Root);

        Assert.NotNull(blocked);
        Assert.Equal(adds, blocked.Sha);
        Assert.Equal("Add the stack", blocked.Subject);
        Assert.Equal(["cfg/stack.yaml"], blocked.UntrackedInTheWay);

        // Continuing as it is just fails the same way again.
        Assert.Equal(OperationOutcome.Stopped, await GitActions.RebaseContinueAsync(_repo.Root));
        Assert.Equal(adds, (await RebaseBlock.FindAsync(_repo.Root))?.Sha);
    }

    [Fact]
    public async Task Moving_the_files_aside_lets_the_rebase_finish_and_they_come_back()
    {
        SetUpStuckRebase();
        await GitActions.RebaseAsync(_repo.Root, "main");
        var blocked = (await RebaseBlock.FindAsync(_repo.Root))!;

        await RebaseBlock.MoveAsideAsync(_repo.Root, blocked.UntrackedInTheWay);
        Assert.Equal(OperationOutcome.Completed, await GitActions.RebaseContinueAsync(_repo.Root));
        var restored = await RebaseBlock.RestoreAsync(_repo.Root);

        Assert.NotNull(restored);
        Assert.Equal(["cfg/stack.yaml"], restored.Restored);
        Assert.Empty(restored.LeftAside);
        Assert.Equal("local secret", File.ReadAllText(Path.Combine(_repo.Root, "cfg/stack.yaml")));
        Assert.Equal("", _repo.Git("status", "--porcelain"));
        Assert.Null(await RebaseBlock.RestoreAsync(_repo.Root));
    }

    [Fact]
    public async Task A_file_with_something_back_at_its_path_stays_aside()
    {
        _repo.Commit("base", "a.txt", "a");
        _repo.Write("keep.txt", "mine");
        await RebaseBlock.MoveAsideAsync(_repo.Root, ["keep.txt"]);
        _repo.Write("keep.txt", "someone else's");

        var restored = await RebaseBlock.RestoreAsync(_repo.Root);

        Assert.NotNull(restored);
        Assert.Equal(["keep.txt"], restored.LeftAside);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(restored.Folder, "keep.txt")));
        Assert.Equal("someone else's", File.ReadAllText(Path.Combine(_repo.Root, "keep.txt")));
    }

    [Fact]
    public async Task A_rebase_stopped_on_a_conflict_is_not_blocked()
    {
        _repo.Commit("base", "a.txt", "one");
        _repo.Git("switch", "-q", "-c", "feature");
        _repo.Commit("feature edit", "a.txt", "two");
        _repo.Git("switch", "-q", "main");
        _repo.Commit("main edit", "a.txt", "three");
        _repo.Git("switch", "-q", "feature");

        Assert.Equal(OperationOutcome.Stopped, await GitActions.RebaseAsync(_repo.Root, "main"));

        Assert.Null(await RebaseBlock.FindAsync(_repo.Root));
    }
}
