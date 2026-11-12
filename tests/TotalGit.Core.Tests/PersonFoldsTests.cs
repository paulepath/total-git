using TotalGit.Core.Git;
using TotalGit.Core.Graph;

namespace TotalGit.Core.Tests;

public class PersonFoldsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private const string Bot = "bot@example.com";

    private static CommitInfo C(string sha, string email, params string[] parents) => new(sha, parents, email, email, T0, sha);

    private static readonly HashSet<string> Folded = new(StringComparer.OrdinalIgnoreCase) { Bot };
    private static readonly HashSet<string> None = [];

    private static string[] Hidden(IReadOnlyList<CommitInfo> commits, IReadOnlySet<string>? pinned = null) =>
        PersonFolds.Find(commits, Folded, pinned ?? None).SelectMany(f => f.Commits).Select(c => c.Sha).ToArray();

    [Fact]
    public void A_branch_of_only_their_commits_folds()
    {
        // b2 - b1 branch off m1; m2 is someone else's work on the main line.
        CommitInfo[] commits = [C("m2", "me@example.com", "m1"), C("b2", Bot, "b1"), C("b1", Bot, "m1"), C("m1", "me@example.com")];

        Assert.Equal(["b2", "b1"], Hidden(commits));
    }

    [Fact]
    public void A_branch_someone_else_committed_to_stays_whole()
    {
        CommitInfo[] commits = [C("x", "me@example.com", "b2"), C("b2", Bot, "b1"), C("b1", Bot, "m1"), C("m1", "me@example.com")];

        Assert.Empty(Hidden(commits));
    }

    [Fact]
    public void Their_work_that_was_merged_stays()
    {
        // The merge is someone else's, so the commits it brought in stay; their newer branch still folds.
        CommitInfo[] commits =
        [
            C("n1", Bot, "merge"),
            C("merge", "me@example.com", "m1", "b1"),
            C("b1", Bot, "m1"),
            C("m1", "me@example.com"),
        ];

        Assert.Equal(["n1"], Hidden(commits));
    }

    [Fact]
    public void A_pinned_commit_and_what_it_is_built_on_stay()
    {
        CommitInfo[] commits = [C("b3", Bot, "b2"), C("b2", Bot, "b1"), C("b1", Bot, "m1"), C("m1", "me@example.com")];

        Assert.Equal(["b3"], Hidden(commits, new HashSet<string> { "b2" }));
    }

    [Fact]
    public void Emails_match_whatever_their_case()
    {
        CommitInfo[] commits = [C("b1", "BOT@Example.com", "m1"), C("m1", "me@example.com")];

        Assert.Equal(["b1"], Hidden(commits));
    }

    [Fact]
    public void Each_person_gets_their_own_fold_newest_first()
    {
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Bot, "other@example.com" };
        CommitInfo[] commits = [C("o1", "other@example.com", "m1"), C("b2", Bot, "b1"), C("b1", Bot, "m1"), C("m1", "me@example.com")];

        var folds = PersonFolds.Find(commits, folded, None);

        Assert.Equal(2, folds.Count);
        Assert.Equal(["b2", "b1"], folds.Single(f => f.Email == Bot).Commits.Select(c => c.Sha));
    }
}
