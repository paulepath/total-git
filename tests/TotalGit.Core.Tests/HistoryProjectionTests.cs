using TotalGit.Core.Git;
using TotalGit.Core.Graph;

namespace TotalGit.Core.Tests;

public class HistoryProjectionTests
{
    private static CommitInfo C(string sha, string author, params string[] parents) =>
        new(sha, parents, author, author + "@example.com", DateTimeOffset.UnixEpoch, sha);

    private static Func<CommitInfo, bool> By(string author) => c => c.AuthorName == author;

    private static string[] Shas(ProjectedHistory p) => p.Commits.Select(c => c.Sha).ToArray();

    private static string[] Parents(ProjectedHistory p, string sha) => p.Commits.Single(c => c.Sha == sha).ParentShas.ToArray();

    [Fact]
    public void Keeping_everything_returns_the_history_unchanged()
    {
        IReadOnlyList<CommitInfo> commits = [C("b", "x", "a"), C("a", "x")];

        var p = HistoryProjection.Project(commits, _ => true);

        Assert.Same(commits, p.Commits);
        Assert.Equal(0, p.Hidden);
    }

    [Fact]
    public void Hidden_commits_are_bridged_to_the_nearest_shown_ancestor()
    {
        var p = HistoryProjection.Project([C("d", "me", "c"), C("c", "you", "b"), C("b", "you", "a"), C("a", "me")], By("me"));

        Assert.Equal(["d", "a"], Shas(p));
        Assert.Equal(["a"], Parents(p, "d"));
        Assert.Equal(2, p.Hidden);
        Assert.Equal("d", p.ShownAs["c"]);
        Assert.Equal("d", p.ShownAs["b"]);
    }

    [Fact]
    public void A_merge_whose_sides_are_hidden_joins_where_they_meet()
    {
        // m merges f into b; both sides hidden, they meet at a.
        var p = HistoryProjection.Project([C("m", "me", "b", "f"), C("f", "you", "a"), C("b", "you", "a"), C("a", "me")], By("me"));

        Assert.Equal(["m", "a"], Shas(p));
        Assert.Equal(["a"], Parents(p, "m"));
    }

    [Fact]
    public void A_hidden_branch_tip_has_no_stand_in()
    {
        // t has nothing shown above it: its label must not move down onto a, where the branch doesn't point.
        var p = HistoryProjection.Project([C("b", "me", "a"), C("t", "you", "a"), C("a", "me")], By("me"));

        Assert.False(p.ShownAs.ContainsKey("t"));
    }

    [Fact]
    public void Parents_outside_the_loaded_history_stay_as_they_are()
    {
        var p = HistoryProjection.Project([C("b", "me", "a"), C("a", "you", "older")], By("me"));

        Assert.Equal(["older"], Parents(p, "b"));
    }

    [Fact]
    public void Work_in_progress_rows_are_always_kept()
    {
        var wip = new CommitInfo(CommitInfo.WorkingTreeSha, ["a"], "", "", DateTimeOffset.UnixEpoch, "// WIP", IsWorkingTree: true);

        var p = HistoryProjection.Project([wip, C("a", "you")], By("me"));

        Assert.Equal([CommitInfo.WorkingTreeSha], Shas(p));
    }

    [Fact]
    public void A_fold_shows_its_newest_commit_with_the_oldest_ones_parents()
    {
        var fold = new CommitFold(["d", "c", "b"], ["me"], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true);

        var p = HistoryProjection.Project([C("d", "me", "c"), C("c", "me", "b"), C("b", "me", "a"), C("a", "me")], null, [fold]);

        Assert.Equal(["d", "a"], Shas(p));
        Assert.Equal(["a"], Parents(p, "d"));
        Assert.Same(fold, p.Folds["d"]);
        Assert.Equal("d", p.ShownAs["b"]);
    }

    [Fact]
    public void A_projected_history_lays_out_as_one_line()
    {
        var p = HistoryProjection.Project([C("d", "me", "c"), C("c", "you", "b"), C("b", "you", "a"), C("a", "me")], By("me"));

        var layout = GraphLayout.Compute(p.Commits);

        Assert.All(layout.Rows, r => Assert.Equal(0, r.Lane));
        Assert.Equal(1, layout.LaneCount);
    }

    [Fact]
    public void Long_hidden_stretches_do_not_overflow_the_stack()
    {
        var commits = Enumerable.Range(0, 50_000).Reverse()
            .Select(i => C($"c{i}", i is 0 or 49_999 ? "me" : "you", i == 0 ? Array.Empty<string>() : new[] { $"c{i - 1}" }))
            .ToList();

        var p = HistoryProjection.Project(commits, By("me"));

        Assert.Equal(["c49999", "c0"], Shas(p));
        Assert.Equal(["c0"], Parents(p, "c49999"));
    }
}
