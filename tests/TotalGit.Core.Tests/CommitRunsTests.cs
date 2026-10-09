using TotalGit.Core.Git;
using TotalGit.Core.Graph;

namespace TotalGit.Core.Tests;

public class CommitRunsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A commit authored at <paramref name="authored"/> minutes and written at <paramref name="written"/> minutes after T0.</summary>
    private static CommitInfo C(string sha, int authored, int written, params string[] parents) =>
        new(sha, parents, "A", "a@example.com", T0.AddMinutes(authored), sha) { CommitDate = T0.AddMinutes(written) };

    private static CommitInfo C(string sha, params string[] parents) => C(sha, 0, 0, parents);

    [Fact]
    public void Three_or_more_consecutive_commits_on_one_line_are_a_run()
    {
        var runs = CommitRuns.Find([C("d", "c"), C("c", "b"), C("b", "a"), C("a")]);

        // The root has no parent, so it can't continue the line it ends.
        Assert.Equal(["d", "c", "b"], Assert.Single(runs).Shas);
    }

    [Fact]
    public void Two_commits_are_not_worth_folding()
    {
        Assert.Empty(CommitRuns.Find([C("b", "a"), C("a", "z")]));
    }

    private static readonly CommitInfo[] Line = [C("e", "d"), C("d", "c"), C("c", "b"), C("b", "a"), C("a", "z")];

    private static CommitInfo By(CommitInfo c, string name, string email) => c with { AuthorName = name, AuthorEmail = email };

    [Fact]
    public void A_run_is_one_authors_commits()
    {
        // Someone else's commit on top stays a row of its own; the three below are one person's and fold.
        var commits = Line.Select((c, i) => i == 0 ? By(c, "Tim", "tim@example.com") : c).ToArray();

        Assert.Equal(["d", "c", "b", "a"], Assert.Single(CommitRuns.Find(commits)).Shas);
    }

    [Fact]
    public void Another_author_part_way_down_splits_the_run()
    {
        var commits = Line.Select((c, i) => i == 2 ? By(c, "Tim", "tim@example.com") : c).ToArray();

        // e, d (2) and b, a (2) are each too short to fold.
        Assert.Empty(CommitRuns.Find(commits));
    }

    [Fact]
    public void The_same_person_from_another_address_is_still_one_author()
    {
        var commits = Line.Select((c, i) => i % 2 == 0 ? By(c, "A", "a@work.example") : c).ToArray();

        Assert.Equal(["e", "d", "c", "b", "a"], Assert.Single(CommitRuns.Find(commits)).Shas);
    }

    [Fact]
    public void A_branch_part_way_down_a_run_starts_its_own_run()
    {
        // e and d are too few to fold, so c's branch heads the only run, and its label stays on its own row.
        var runs = CommitRuns.Find(Line, new HashSet<string> { "e", "c" });

        Assert.Equal(["c", "b", "a"], Assert.Single(runs).Shas);
    }

    [Fact]
    public void A_branch_on_the_newest_commit_keeps_the_run_whole()
    {
        Assert.Equal(["e", "d", "c", "b", "a"], Assert.Single(CommitRuns.Find(Line, new HashSet<string> { "e" })).Shas);
    }

    [Fact]
    public void Branches_on_neighbouring_commits_leave_the_top_one_unfolded()
    {
        Assert.Equal(["d", "c", "b", "a"], Assert.Single(CommitRuns.Find(Line, new HashSet<string> { "e", "d" })).Shas);
    }

    [Fact]
    public void Another_branch_commit_between_rows_ends_the_run()
    {
        // x (another branch) sits between c and b in the graph's order, so c..b aren't consecutive rows.
        var runs = CommitRuns.Find([C("e", "d"), C("d", "c"), C("c", "b"), C("x", "y"), C("b", "a"), C("a", "z"), C("y", "q")]);

        Assert.Equal(["e", "d", "c"], Assert.Single(runs).Shas);
    }

    [Fact]
    public void A_fork_point_ends_the_run()
    {
        // f branches off c, so c can't be folded under d.
        var runs = CommitRuns.Find([C("e", "d"), C("d", "c"), C("f", "c"), C("c", "b"), C("b", "a"), C("a", "z")]);

        Assert.Equal(["c", "b", "a"], Assert.Single(runs).Shas);
    }

    [Fact]
    public void Merges_are_never_part_of_a_run()
    {
        var runs = CommitRuns.Find([C("m", "c", "x"), C("c", "b"), C("b", "a"), C("a", "z"), C("x", "z")]);

        Assert.Equal(["c", "b", "a"], Assert.Single(runs).Shas);
    }

    [Fact]
    public void A_run_rewritten_together_onto_newer_work_is_rebased()
    {
        // Authored at minutes 0-2, rewritten together at minute 600 onto "base", which was written at minute 300.
        var commits = new[] { C("c", 2, 600, "b"), C("b", 1, 600, "a"), C("a", 0, 600, "base"), C("base", 300, 300) };

        Assert.True(Assert.Single(CommitRuns.Find(commits)).IsRebased);
    }

    [Fact]
    public void A_run_written_as_authored_is_not_rebased()
    {
        var commits = new[] { C("c", 2, 2, "b"), C("b", 1, 1, "a"), C("a", 0, 0, "base"), C("base", -60, -60) };

        Assert.False(Assert.Single(CommitRuns.Find(commits)).IsRebased);
    }

    [Fact]
    public void A_run_rebased_onto_older_work_is_not_flagged()
    {
        // Rewritten together (e.g. amended in place) but the commit below is older than the run: nothing jumped ahead.
        var commits = new[] { C("c", 2, 600, "b"), C("b", 1, 600, "a"), C("a", 0, 600, "base"), C("base", -60, -60) };

        Assert.False(Assert.Single(CommitRuns.Find(commits)).IsRebased);
    }

    [Fact]
    public void Commits_rewritten_at_different_times_are_not_one_rebase()
    {
        var commits = new[] { C("c", 2, 900, "b"), C("b", 1, 600, "a"), C("a", 0, 600, "base"), C("base", 300, 300) };

        Assert.False(Assert.Single(CommitRuns.Find(commits)).IsRebased);
    }

    [Fact]
    public void A_run_with_newer_commits_on_top_folds_whole_but_is_not_called_rebased()
    {
        var commits = new[]
        {
            C("e", 700, 700, "d"), C("d", 650, 650, "c"),
            C("c", 2, 600, "b"), C("b", 1, 600, "a"), C("a", 0, 600, "base"), C("base", 300, 300),
        };

        var run = Assert.Single(CommitRuns.Find(commits));

        Assert.Equal(["e", "d", "c", "b", "a"], run.Shas);
        Assert.False(run.IsRebased);
    }

    [Fact]
    public void Commit_dates_come_from_the_repository()
    {
        using var repo = new TestRepo();
        repo.Commit("one");
        using var session = RepositorySession.Open(repo.Root);

        var commit = Assert.Single(session.ReadHistory());

        Assert.NotNull(commit.CommitDate);
    }
}
