using TotalGit.Core.Git;
using TotalGit.Core.Hosting;

namespace TotalGit.Core.Tests;

public class BranchOwnershipTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CommitInfo C(string sha, string author, int minutes, params string[] parents) =>
        new(sha, parents, author, author.ToLowerInvariant() + "@example.com", T0.AddMinutes(minutes), sha);

    private static RefInfo Remote(string name, string sha) => new("origin/" + name, RefKind.RemoteBranch, sha, false, "origin");

    // main: m1 <- m2 <- m3
    private static readonly List<CommitInfo> Main = [C("m3", "Ann", 30, "m2"), C("m2", "Ann", 20, "m1"), C("m1", "Ann", 10)];

    private static readonly RefInfo MainRef = new("main", RefKind.LocalBranch, "m3", true);

    private static (Dictionary<string, BranchOwner> Owners, List<RefInfo> Unresolved) Compute(List<CommitInfo> commits, params RefInfo[] branches) =>
        BranchOwnership.Compute(commits, branches, [MainRef, .. branches.Where(b => b.ShortName == "main")], r => r.ShortName == "main");

    [Fact]
    public void Owner_is_the_main_author_of_commits_not_on_a_main_line()
    {
        // m2 <- b1 (Bob) <- b2 (Bob) <- b3 (Cat): main's commits don't count.
        var commits = new List<CommitInfo>(Main) { C("b3", "Cat", 60, "b2"), C("b2", "Bob", 50, "b1"), C("b1", "Bob", 40, "m2") };
        var (owners, unresolved) = Compute(commits, Remote("x", "b3"));

        Assert.Empty(unresolved);
        var owner = owners["origin/x"];
        Assert.Equal("Bob", owner.Owner.Name);
        Assert.Equal(2, owner.Owner.Commits);
        Assert.Equal(3, owner.OwnCommits);
        Assert.Equal("Cat", Assert.Single(owner.Others).Name);
        Assert.False(owner.IsGuess);
    }

    [Fact]
    public void Merges_from_main_into_the_branch_are_left_out()
    {
        // b1 (Bob) <- merge of main by Dan <- b2 (Bob)
        var commits = new List<CommitInfo>(Main) { C("b2", "Bob", 70, "mg"), C("mg", "Dan", 60, "b1", "m3"), C("b1", "Bob", 40, "m2") };
        var owner = Compute(commits, Remote("x", "b2")).Owners["origin/x"];

        Assert.Equal("Bob", owner.Owner.Name);
        Assert.Equal(2, owner.OwnCommits);
        Assert.Empty(owner.Others);
    }

    [Fact]
    public void A_merged_branch_is_a_guess_from_its_newest_commit()
    {
        var owner = Compute(Main, Remote("old", "m2")).Owners["origin/old"];

        Assert.True(owner.IsGuess);
        Assert.Equal("Ann", owner.Owner.Name);
        Assert.Equal(0, owner.OwnCommits);
    }

    [Fact]
    public void A_main_line_with_nothing_of_its_own_has_no_owner()
    {
        // features/a sits on main's tip: nothing of its own, and as a main line it's shared rather than guessed.
        var feature = Remote("features/a", "m3");
        var (owners, unresolved) = BranchOwnership.Compute(Main, [feature], [MainRef, feature],
            r => r.ShortName is "main" or "features/a");
        Assert.Empty(owners);
        Assert.Empty(unresolved);
    }

    [Fact]
    public void Main_has_no_owner_when_the_other_main_lines_grew_from_it()
    {
        // origin/main and main are one line; features/a grew from it, so main has nothing of its own.
        var feature = Remote("features/a", "f1");
        var commits = new List<CommitInfo>(Main) { C("f1", "Eve", 40, "m3") };
        var (owners, _) = BranchOwnership.Compute(commits, [Remote("main", "m3"), feature], [MainRef, Remote("main", "m3"), feature],
            r => r.ShortName is "main" or "features/a");

        Assert.False(owners.ContainsKey("origin/main"));
        Assert.Equal("Eve", owners["origin/features/a"].Owner.Name);
    }

    [Fact]
    public void A_tie_goes_to_whoever_committed_last()
    {
        var commits = new List<CommitInfo>(Main) { C("b2", "Cat", 50, "b1"), C("b1", "Bob", 40, "m3") };
        Assert.Equal("Cat", Compute(commits, Remote("x", "b2")).Owners["origin/x"].Owner.Name);
    }

    [Fact]
    public void A_main_line_is_measured_against_the_other_main_lines()
    {
        // features/a is a main line too: its own commits are those not on main.
        var commits = new List<CommitInfo>(Main) { C("f1", "Eve", 40, "m3") };
        var feature = Remote("features/a", "f1");
        var (owners, _) = BranchOwnership.Compute(commits, [feature], [MainRef, feature],
            r => r.ShortName is "main" or "features/a");

        Assert.Equal("Eve", owners["origin/features/a"].Owner.Name);
        Assert.False(owners["origin/features/a"].IsGuess);
    }

    [Fact]
    public void Branches_beyond_the_loaded_history_are_left_for_git()
    {
        // b1's parent isn't loaded, so where its own commits end is unknown.
        var commits = new List<CommitInfo>(Main) { C("b1", "Bob", 40, "zz") };
        var (owners, unresolved) = Compute(commits, Remote("x", "b1"), Remote("y", "nowhere"));

        Assert.Empty(owners);
        Assert.Equal(["origin/x", "origin/y"], unresolved.Select(r => r.Name));
    }

    [Fact]
    public async Task Own_commits_from_git_stop_at_main_lines()
    {
        using var repo = new TestRepo();
        var main = repo.Commit("base");
        repo.Git("switch", "-q", "-c", "topic");
        repo.Git("-c", "user.name=Bob", "-c", "user.email=bob@example.com", "commit", "-q", "--allow-empty", "-m", "one");
        repo.Git("-c", "user.name=Bob", "-c", "user.email=bob@example.com", "commit", "-q", "--allow-empty", "-m", "two");
        var tip = repo.Git("rev-parse", "HEAD");

        var own = await GitActions.OwnCommitsAsync(repo.Root, tip, [main]);

        Assert.Equal(2, own.Count);
        Assert.All(own, c => Assert.Equal("Bob", c.Name));
        Assert.Equal("Bob", BranchOwnership.FromOwnCommits(own, null)!.Owner.Name);
    }

    [Fact]
    public async Task Deleting_a_remote_branch_removes_it_there_and_locally_tracked()
    {
        using var remote = new TestRepo(bare: true);
        using var repo = new TestRepo();
        repo.Commit("base");
        repo.Git("remote", "add", "origin", remote.Root);
        repo.Git("push", "-q", "-u", "origin", "main");
        repo.Git("push", "-q", "origin", "main:feature/x");
        repo.Git("fetch", "-q");

        await GitActions.DeleteRemoteBranchAsync(repo.Root, "origin", "feature/x");

        Assert.Equal("", remote.Git("branch", "--list", "feature/x"));
        Assert.Equal("", repo.Git("branch", "-r", "--list", "origin/feature/x"));
        Assert.NotEqual("", remote.Git("branch", "--list", "main"));
    }

    [Theory]
    [InlineData("feature/E4-2361-docker", "E4-2361", 8)]
    [InlineData("E4-12", "E4-12", 0)]
    [InlineData("feature/e4-2361-docker", "E4-2361", 8)] // lower case: E4 is a known project
    [InlineData("bug/fix-2", null, 0)] // lower case, unknown project
    [InlineData("release-1.2", null, 0)]
    [InlineData("feature/abc/TG-7_thing", "TG-7", 12)]
    public void Finds_ticket_keys_in_branch_names(string name, string? key, int start)
    {
        var found = PullRequestTriage.KeyInBranch(name, new HashSet<string>(["E4"], StringComparer.OrdinalIgnoreCase));
        Assert.Equal(key, found?.Key);
        if (found is { } f) Assert.Equal(start, f.Start);
    }
}
