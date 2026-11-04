using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public class CheckoutBlockersTests
{
    /// <summary>main, plus "other" which tracks cfg/settings.json (untracked on main, as after a .gitignore change).</summary>
    private static TestRepo RepoWithFileInTheWay()
    {
        var repo = new TestRepo();
        repo.Commit("base", "a.txt", "base");
        repo.Git("switch", "-q", "-c", "other");
        repo.Commit("settings", "cfg/settings.json", "theirs");
        repo.Git("switch", "-q", "main");
        repo.Write("cfg/settings.json", "mine");
        repo.Write("notes.txt", "not in the way");
        return repo;
    }

    [Fact]
    public async Task Git_refuses_and_names_the_untracked_file_in_the_way()
    {
        using var repo = RepoWithFileInTheWay();
        var ex = await Assert.ThrowsAsync<GitCommandException>(() => GitActions.CheckoutAsync(repo.Root, "other"));
        var blockers = GitActions.ParseCheckoutBlockers(ex.Message);
        Assert.NotNull(blockers);
        Assert.Equal(["cfg/settings.json"], blockers.Untracked);
        Assert.Empty(blockers.Changed);
        Assert.Equal(["cfg/settings.json"], await GitActions.UntrackedInTheWayAsync(repo.Root, "other"));
    }

    [Fact]
    public async Task Discard_deletes_untracked_files_in_the_way_with_a_backup_and_keeps_the_rest()
    {
        using var repo = RepoWithFileInTheWay();
        repo.Write("a.txt", "changed");
        await GitActions.CheckoutAsync(repo.Root, "other", LocalChanges.Discard);
        Assert.Equal("other", repo.Git("branch", "--show-current"));
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(repo.Root, "cfg/settings.json")));
        Assert.Equal("base", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "notes.txt")));

        await (await DiscardBackup.LatestAsync(repo.Root))!.UndoAsync(repo.Root);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(repo.Root, "cfg/settings.json")));
        Assert.Equal("changed", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
    }

    [Fact]
    public async Task Stash_takes_changes_and_untracked_files_in_the_way_but_no_other_untracked_files()
    {
        using var repo = RepoWithFileInTheWay();
        repo.Write("a.txt", "changed");
        await GitActions.StashAsync(repo.Root, "before", includeUntracked: false,
            alsoUntracked: await GitActions.UntrackedInTheWayAsync(repo.Root, "other"));
        Assert.Equal("?? notes.txt", repo.Git("status", "--porcelain").Trim());
        await GitActions.CheckoutAsync(repo.Root, "other");
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(repo.Root, "cfg/settings.json")));
        Assert.Contains("cfg/settings.json", repo.Git("show", "--name-only", "--format=", "stash@{0}^3"));
    }

    [Fact]
    public async Task Files_where_the_target_has_a_folder_or_inside_one_it_has_as_a_file_are_in_the_way()
    {
        using var repo = new TestRepo();
        repo.Commit("base", "a.txt", "base");
        repo.Git("switch", "-q", "-c", "other");
        repo.Commit("folder", "tools/run.ps1", "theirs");
        repo.Commit("file", "build", "theirs");
        repo.Git("switch", "-q", "main");
        repo.Write("tools", "a file where they have a folder");
        repo.Write("build/out.txt", "inside a folder where they have a file");
        repo.Write("tools2/x.txt", "not in the way");
        Assert.Equal(["build/out.txt", "tools"], (await GitActions.UntrackedInTheWayAsync(repo.Root, "other")).Order());
    }

    [Fact]
    public void Reads_both_kinds_of_refusal()
    {
        const string message = """
            error: Your local changes to the following files would be overwritten by checkout:
            	src/app.cs
            Please commit your changes or stash them before you switch branches.
            error: The following untracked working tree files would be removed by checkout:
            	infra/local/servicebus-config.json
            	"odd name.txt"
            Please move or remove them before you switch branches.
            Aborting
            """;
        var blockers = GitActions.ParseCheckoutBlockers(message.ReplaceLineEndings("\n"));
        Assert.NotNull(blockers);
        Assert.Equal(["src/app.cs"], blockers.Changed);
        Assert.Equal(["infra/local/servicebus-config.json", "odd name.txt"], blockers.Untracked);
        Assert.Null(GitActions.ParseCheckoutBlockers("fatal: invalid reference: nope"));
    }
}
