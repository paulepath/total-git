using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class GitHeadTests
{
    [Fact]
    public void Reads_the_checked_out_branch()
    {
        using var repo = new TestRepo();
        repo.Commit("one");
        repo.Git("switch", "-q", "-c", "feature/tiles");

        Assert.Equal("feature/tiles", GitHead.Read(repo.Root));
    }

    [Fact]
    public void A_detached_head_gives_the_short_commit_id()
    {
        using var repo = new TestRepo();
        var sha = repo.Commit("one");
        repo.Git("switch", "-q", "--detach", sha);

        Assert.Equal(sha[..7], GitHead.Read(repo.Root));
    }

    [Fact]
    public void A_worktree_follows_its_git_file()
    {
        using var repo = new TestRepo();
        repo.Commit("one");
        var worktree = Path.Combine(Path.GetTempPath(), "totalgit-head-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            repo.Git("worktree", "add", "-q", "-b", "side", worktree);

            Assert.Equal("side", GitHead.Read(worktree));
        }
        finally
        {
            repo.Git("worktree", "remove", "--force", worktree);
        }
    }

    [Fact]
    public void A_missing_or_plain_folder_has_no_head()
    {
        Assert.Null(GitHead.Read(Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("N"))));
        var plain = Directory.CreateTempSubdirectory("totalgit-plain-").FullName;
        try
        {
            Assert.Null(GitHead.Read(plain));
        }
        finally
        {
            Directory.Delete(plain, true);
        }
    }
}
