using LibGit2Sharp;
using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class RepositoryReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "totalgit-test-" + Guid.NewGuid().ToString("N"));

    public RepositoryReaderTests() => Repository.Init(_dir);

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private static Commit CommitFile(Repository repo, string file, string content, string message, DateTimeOffset when)
    {
        File.WriteAllText(Path.Combine(repo.Info.WorkingDirectory, file), content);
        Commands.Stage(repo, file);
        var sig = new Signature("Ada Lovelace", "ada@example.com", when);
        return repo.Commit(message, sig, sig);
    }

    [Fact]
    public void Reads_commits_branches_and_tags()
    {
        var t0 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using (var repo = new Repository(_dir))
        {
            var first = CommitFile(repo, "a.txt", "1", "first", t0);
            repo.ApplyTag("v1.0");
            var feature = repo.CreateBranch("feature");
            CommitFile(repo, "a.txt", "2", "second on main", t0.AddMinutes(1));
            Commands.Checkout(repo, feature);
            CommitFile(repo, "b.txt", "x", "feature work", t0.AddMinutes(2));
            Commands.Checkout(repo, repo.Branches["master"] ?? repo.Branches["main"]);
            Assert.NotNull(first);
        }

        var snap = RepositoryReader.Read(Path.Combine(_dir));

        Assert.Equal(3, snap.Commits.Count);
        Assert.Equal("first", snap.Commits[^1].MessageShort);
        Assert.Equal("Ada Lovelace", snap.Commits[0].AuthorName);
        Assert.Contains(snap.Refs, r => r is { Name: "feature", Kind: RefKind.LocalBranch, IsCurrent: false });
        Assert.Contains(snap.Refs, r => r is { Kind: RefKind.LocalBranch, IsCurrent: true });
        Assert.Contains(snap.Refs, r => r is { Name: "v1.0", Kind: RefKind.Tag });
        Assert.Equal(snap.Commits[^1].Sha, snap.Refs.Single(r => r.Name == "v1.0").TargetSha);
    }

    [Fact]
    public void Discovers_repository_from_subfolder()
    {
        using (var repo = new Repository(_dir))
            CommitFile(repo, "a.txt", "1", "first", DateTimeOffset.Now);
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "sub", "deeper")).FullName;

        var snap = RepositoryReader.Read(sub);

        Assert.Single(snap.Commits);
    }

    [Fact]
    public void Non_repository_throws_friendly_error()
    {
        var plain = Directory.CreateTempSubdirectory("totalgit-plain").FullName;
        try
        {
            Assert.Throws<RepositoryOpenException>(() => RepositoryReader.Read(plain));
        }
        finally
        {
            Directory.Delete(plain);
        }
    }
}
