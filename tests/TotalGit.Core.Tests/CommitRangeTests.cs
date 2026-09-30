using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class CommitRangeTests : IDisposable
{
    //   a - b - c - d        (main line)
    //        \       \
    //         x - y   m      (m merges y into d)
    private static CommitInfo C(string sha, params string[] parents) => new(sha, parents, "n", "e", DateTimeOffset.Now, "msg " + sha);

    private static readonly Dictionary<string, CommitInfo> History = new[]
    {
        C("m", "d", "y"), C("d", "c"), C("y", "x"), C("c", "b"), C("x", "b"), C("b", "a"), C("a"),
    }.ToDictionary(c => c.Sha);

    [Theory]
    [InlineData("d", "b")]
    [InlineData("b", "d")]
    public void Resolves_either_click_order_newest_first(string first, string second)
    {
        var range = CommitRange.Resolve(History, first, second);
        Assert.True(range.IsValid, range.Error);
        Assert.Equal(["d", "c", "b"], range.Commits.Select(c => c.Sha));
    }

    [Fact]
    public void A_single_commit_is_a_range() =>
        Assert.Equal(["c"], CommitRange.Resolve(History, "c", "c").Commits.Select(c => c.Sha));

    [Fact]
    public void Commits_on_different_lines_are_rejected() =>
        Assert.Contains("one line", CommitRange.Resolve(History, "y", "c").Error);

    [Fact]
    public void A_merge_in_the_range_is_rejected() =>
        Assert.Contains("merge commit", CommitRange.Resolve(History, "m", "c").Error);

    [Fact]
    public void The_wip_row_and_unloaded_commits_are_rejected()
    {
        var withWip = new Dictionary<string, CommitInfo>(History)
        {
            [CommitInfo.WorkingTreeSha] = new(CommitInfo.WorkingTreeSha, ["d"], "", "", DateTimeOffset.Now, "// WIP", IsWorkingTree: true),
        };
        Assert.Contains("WIP", CommitRange.Resolve(withWip, CommitInfo.WorkingTreeSha, "c").Error);
        Assert.Contains("loaded history", CommitRange.Resolve(History, "d", "zzz").Error);
    }

    // ------------------------------------------------------------------ rebasing a range with git

    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    private string Log(string range) => _repo.Git("log", "--format=%s", range).Replace("\r", "").Trim();

    /// <summary>
    /// main: A-B-S where S is the squash-merge of branch1's C and D; branch1: A-B-C-D-E-F.
    /// </summary>
    private (string D, string E, string F) StackedAfterSquashMerge()
    {
        _repo.Commit("A", "a.txt", "a");
        _repo.Commit("B", "b.txt", "b");
        _repo.Git("switch", "-q", "-c", "branch1");
        _repo.Commit("C", "c.txt", "c");
        var d = _repo.Commit("D", "d.txt", "d");
        var e = _repo.Commit("E", "e.txt", "e");
        var f = _repo.Commit("F", "f.txt", "f");
        _repo.Git("switch", "-q", "main");
        _repo.Write("c.txt", "c");
        _repo.Write("d.txt", "d");
        _repo.Git("add", "-A");
        _repo.Git("commit", "-q", "-m", "S");
        return (d, e, f);
    }

    [Fact]
    public async Task Rebasing_the_tip_range_replays_only_the_selected_commits()
    {
        var (d, _, _) = StackedAfterSquashMerge();

        var outcome = await GitActions.RebaseOntoAsync(_repo.Root, "main", d, "branch1");

        Assert.Equal(OperationOutcome.Completed, outcome);
        Assert.Equal("F\nE\nS\nB\nA", Log("branch1"));
    }

    [Fact]
    public async Task A_range_below_the_tip_goes_into_a_new_branch_and_leaves_the_original_alone()
    {
        var (d, e, _) = StackedAfterSquashMerge();
        var before = _repo.Git("rev-parse", "branch1").Trim();

        await GitActions.CreateBranchAsync(_repo.Root, "branch1-rebased", e, checkout: false);
        var outcome = await GitActions.RebaseOntoAsync(_repo.Root, "main", d, "branch1-rebased");

        Assert.Equal(OperationOutcome.Completed, outcome);
        Assert.Equal("E\nS\nB\nA", Log("branch1-rebased"));
        Assert.Equal(before, _repo.Git("rev-parse", "branch1").Trim());
    }

    [Fact]
    public async Task A_conflicting_range_stops_the_rebase()
    {
        _repo.Commit("base", "x.txt", "base");
        _repo.Git("switch", "-q", "-c", "topic");
        var first = _repo.Commit("first", "y.txt", "y");
        _repo.Commit("change x", "x.txt", "topic side");
        _repo.Git("switch", "-q", "main");
        _repo.Commit("main side", "x.txt", "main side");

        var outcome = await GitActions.RebaseOntoAsync(_repo.Root, "main", first, "topic");

        Assert.Equal(OperationOutcome.Stopped, outcome);
        await GitActions.RebaseAbortAsync(_repo.Root);
    }
}
