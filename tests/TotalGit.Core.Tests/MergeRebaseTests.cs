using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class MergeRebaseTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    private string Log(string range = "HEAD") => _repo.Git("log", "--format=%s", range).Replace("\r", "");

    private RepositoryState State()
    {
        using var session = RepositorySession.Open(_repo.Root);
        return session.LoadState();
    }

    [Fact]
    public async Task Merges_without_conflicts()
    {
        _repo.Commit("base", "a.txt", "a");
        _repo.Git("switch", "-q", "-c", "topic");
        _repo.Commit("topic work", "b.txt", "b");
        _repo.Git("switch", "-q", "main");
        _repo.Commit("main work", "c.txt", "c");

        var outcome = await GitActions.MergeAsync(_repo.Root, "topic");

        Assert.Equal(OperationOutcome.Completed, outcome);
        Assert.Equal("Merge branch 'topic'", Log("-1"));
        Assert.Equal(RepoOperation.None, State().Operation);
    }

    [Fact]
    public async Task No_fast_forward_creates_a_merge_commit()
    {
        _repo.Commit("base");
        _repo.Git("switch", "-q", "-c", "topic");
        _repo.Commit("topic work", "b.txt", "b");
        _repo.Git("switch", "-q", "main");

        await GitActions.MergeAsync(_repo.Root, "topic", noFastForward: true);

        Assert.Equal(2, _repo.Git("log", "-1", "--format=%P").Split(' ').Length);
    }

    [Fact]
    public async Task Conflicting_merge_stops_and_can_be_concluded_or_aborted()
    {
        MakeConflict();

        var outcome = await GitActions.MergeAsync(_repo.Root, "topic");

        Assert.Equal(OperationOutcome.Stopped, outcome);
        var state = State();
        Assert.Equal(RepoOperation.Merge, state.Operation);
        using (var session = RepositorySession.Open(_repo.Root))
            Assert.Contains(session.GetStatus().Unstaged, f => f is { Path: "a.txt", Kind: ChangeKind.Conflicted });

        await GitActions.MergeAbortAsync(_repo.Root);
        Assert.Equal(RepoOperation.None, State().Operation);
        Assert.Equal("main", File.ReadAllText(Path.Combine(_repo.Root, "a.txt")));

        await GitActions.MergeAsync(_repo.Root, "topic");
        _repo.Write("a.txt", "resolved");
        _repo.Git("add", "a.txt");
        await GitActions.MergeContinueAsync(_repo.Root);
        Assert.Equal(RepoOperation.None, State().Operation);
        Assert.Equal("Merge branch 'topic'", Log("-1"));
    }

    [Fact]
    public async Task Rebases_onto_another_branch()
    {
        _repo.Commit("base", "a.txt", "a");
        _repo.Git("switch", "-q", "-c", "topic");
        _repo.Commit("topic work", "b.txt", "b");
        _repo.Git("switch", "-q", "main");
        _repo.Commit("main work", "c.txt", "c");
        _repo.Git("switch", "-q", "topic");

        var outcome = await GitActions.RebaseAsync(_repo.Root, "main");

        Assert.Equal(OperationOutcome.Completed, outcome);
        Assert.Equal("topic work\nmain work\nbase", Log());
    }

    [Fact]
    public async Task Conflicting_rebase_reports_progress_and_can_continue()
    {
        MakeConflict();
        _repo.Git("switch", "-q", "topic");

        var outcome = await GitActions.RebaseAsync(_repo.Root, "main");

        Assert.Equal(OperationOutcome.Stopped, outcome);
        var state = State();
        Assert.Equal(RepoOperation.Rebase, state.Operation);
        Assert.Equal("1/1", state.OperationProgress);

        _repo.Write("a.txt", "resolved");
        _repo.Git("add", "a.txt");
        Assert.Equal(OperationOutcome.Completed, await GitActions.RebaseContinueAsync(_repo.Root));
        Assert.Equal(RepoOperation.None, State().Operation);
        Assert.Equal("topic change\nmain change\nbase", Log());
    }

    [Fact]
    public async Task Rebase_abort_restores_the_branch()
    {
        MakeConflict();
        _repo.Git("switch", "-q", "topic");
        var before = _repo.Git("rev-parse", "HEAD");

        await GitActions.RebaseAsync(_repo.Root, "main");
        await GitActions.RebaseAbortAsync(_repo.Root);

        Assert.Equal(before, _repo.Git("rev-parse", "HEAD"));
        Assert.Equal(RepoOperation.None, State().Operation);
    }

    [Fact]
    public async Task Interactive_rebase_drops_reorders_squashes_and_rewords()
    {
        var root = _repo.Commit("base", "base.txt", "0");
        var a = _repo.Commit("A", "a.txt", "a");
        var b = _repo.Commit("B", "b.txt", "b");
        var c = _repo.Commit("C", "c.txt", "c");
        var d = _repo.Commit("D", "d.txt", "d");
        var e = _repo.Commit("E", "e.txt", "e");

        var commits = await GitActions.CommitsSinceAsync(_repo.Root, root);
        Assert.Equal([a, b, c, d, e], commits.Select(x => x.Sha));

        var outcome = await GitActions.InteractiveRebaseAsync(_repo.Root, root,
        [
            new(RebaseAction.Pick, c),
            new(RebaseAction.Reword, a, "A reworded\n\nwith a body"),
            new(RebaseAction.Drop, b),
            new(RebaseAction.Pick, d),
            new(RebaseAction.Fixup, e),
        ]);

        Assert.Equal(OperationOutcome.Completed, outcome);
        Assert.Equal("D\nA reworded\nC\nbase", Log());
        Assert.Equal("with a body", _repo.Git("log", "-1", "--format=%b", "HEAD~1"));
        Assert.False(File.Exists(Path.Combine(_repo.Root, "b.txt")));
        Assert.True(File.Exists(Path.Combine(_repo.Root, "e.txt")));
    }

    [Fact]
    public async Task Interactive_squash_keeps_both_messages()
    {
        var root = _repo.Commit("base", "base.txt", "0");
        var a = _repo.Commit("A", "a.txt", "a");
        var b = _repo.Commit("B", "b.txt", "b");

        await GitActions.InteractiveRebaseAsync(_repo.Root, root, [new(RebaseAction.Pick, a), new(RebaseAction.Squash, b)]);

        Assert.Equal("A\nbase", Log());
        Assert.Contains("B", _repo.Git("log", "-1", "--format=%B"));
    }

    /// <summary>main and topic both change a.txt after "base".</summary>
    private void MakeConflict()
    {
        _repo.Commit("base", "a.txt", "base");
        _repo.Git("switch", "-q", "-c", "topic");
        _repo.Commit("topic change", "a.txt", "topic");
        _repo.Git("switch", "-q", "main");
        _repo.Commit("main change", "a.txt", "main");
    }
}
