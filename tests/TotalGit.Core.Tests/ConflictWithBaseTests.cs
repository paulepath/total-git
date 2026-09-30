using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class ConflictWithBaseTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    /// <summary>main and topic both change the middle line of a.txt; returns the conflicted file's text.</summary>
    private async Task<string> Conflict()
    {
        _repo.Commit("base", "a.txt", "one\ntwo\nthree\n");
        _repo.Git("switch", "-q", "-c", "topic");
        _repo.Commit("topic change", "a.txt", "one\nTWO topic\nthree\n");
        _repo.Git("switch", "-q", "main");
        _repo.Commit("main change", "a.txt", "one\nTWO main\nthree\n");
        var outcome = await GitActions.MergeAsync(_repo.Root, "topic");
        Assert.NotEqual(OperationOutcome.Completed, outcome);
        return File.ReadAllText(Path.Combine(_repo.Root, "a.txt"));
    }

    [Fact]
    public async Task Recovers_the_common_ancestor_for_each_conflict()
    {
        var current = await Conflict();
        var file = ConflictFile.Parse(current);
        Assert.Null(file.Conflicts[0].Base);

        var text = await GitActions.ConflictWithBaseAsync(_repo.Root, "a.txt", current, file.OursLabel, file.TheirsLabel);

        var withBase = ConflictFile.Parse(text!);
        Assert.Equal(["two"], withBase.Conflicts[0].Base!);
        Assert.Equal(["TWO main"], withBase.Conflicts[0].Ours);
        Assert.Equal(["TWO topic"], withBase.Conflicts[0].Theirs);
        Assert.Equal("HEAD", withBase.OursLabel);
        // No temporary files left behind.
        Assert.DoesNotContain("merge_file", _repo.Git("status", "--porcelain", "--untracked-files=all"));
    }

    [Fact]
    public async Task There_is_no_base_for_a_file_added_on_both_sides()
    {
        _repo.Commit("base", "other.txt", "x");
        _repo.Git("switch", "-q", "-c", "topic");
        _repo.Commit("topic adds", "new.txt", "from topic\n");
        _repo.Git("switch", "-q", "main");
        _repo.Commit("main adds", "new.txt", "from main\n");
        Assert.NotEqual(OperationOutcome.Completed, await GitActions.MergeAsync(_repo.Root, "topic"));
        var current = File.ReadAllText(Path.Combine(_repo.Root, "new.txt"));
        var file = ConflictFile.Parse(current);

        Assert.Null(await GitActions.ConflictWithBaseAsync(_repo.Root, "new.txt", current, file.OursLabel, file.TheirsLabel));
        Assert.DoesNotContain("merge_file", _repo.Git("status", "--porcelain", "--untracked-files=all"));
    }

    [Fact]
    public async Task Gives_up_when_the_file_was_edited()
    {
        var current = await Conflict();
        var file = ConflictFile.Parse(current);
        var edited = "edited\n" + current;

        Assert.Null(await GitActions.ConflictWithBaseAsync(_repo.Root, "a.txt", edited, file.OursLabel, file.TheirsLabel));
    }
}
