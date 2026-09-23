using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class RepositorySessionTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Pages_through_history_without_duplicates()
    {
        for (var i = 0; i < 25; i++) _repo.Commit($"c{i}");
        using var session = RepositorySession.Open(_repo.Root);

        var pages = new List<IReadOnlyList<CommitInfo>>();
        while (session.HasMoreHistory) pages.Add(session.ReadHistory(10));

        Assert.Equal([10, 10, 5], pages.Select(p => p.Count));
        var all = pages.SelectMany(p => p).ToList();
        Assert.Equal(25, all.Select(c => c.Sha).Distinct().Count());
        Assert.Equal("c24", all[0].MessageShort);
    }

    [Fact]
    public void Reset_history_restarts_from_newest()
    {
        _repo.Commit("one");
        using var session = RepositorySession.Open(_repo.Root);
        session.ReadHistory();
        _repo.Commit("two");

        session.ResetHistory();

        Assert.Equal("two", session.ReadHistory()[0].MessageShort);
    }

    [Fact]
    public void Flags_local_branches_whose_remote_branch_was_deleted()
    {
        using var remote = new TestRepo(bare: true);
        _repo.Commit("base");
        _repo.Git("remote", "add", "origin", remote.Root);
        _repo.Git("push", "-q", "-u", "origin", "main");
        _repo.Git("push", "-q", "origin", "main:feature/x");
        _repo.Git("branch", "--track", "feature/x", "origin/feature/x");
        remote.Git("branch", "-D", "feature/x");
        _repo.Git("fetch", "-q", "--prune");

        using var session = RepositorySession.Open(_repo.Root);
        var refs = session.LoadState().Refs;

        var gone = refs.Single(r => r is { Kind: RefKind.LocalBranch, Name: "feature/x" });
        Assert.True(gone.UpstreamGone);
        Assert.Equal("origin/feature/x", gone.Upstream);
        Assert.False(refs.Single(r => r is { Kind: RefKind.LocalBranch, Name: "main" }).UpstreamGone);
    }

    [Fact]
    public void Linked_worktree_sees_shared_config()
    {
        _repo.Commit("base");
        _repo.Git("branch", "gone-branch");
        _repo.Git("remote", "add", "origin", "https://github.com/example/demo.git");
        _repo.Git("config", "branch.gone-branch.remote", "origin");
        _repo.Git("config", "branch.gone-branch.merge", "refs/heads/gone-branch");
        var wt = Path.Combine(_repo.Root, ".worktrees", "wt");
        _repo.Git("worktree", "add", "-q", "-b", "wt-branch", wt);
        // main is one ahead of a (faked) origin/main.
        _repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");
        _repo.Git("config", "branch.main.remote", "origin");
        _repo.Git("config", "branch.main.merge", "refs/heads/main");
        _repo.Commit("ahead");

        using var session = RepositorySession.Open(wt);
        for (var i = 0; i < 2; i++)
        {
            var state = session.LoadState();
            Assert.Equal("https://github.com/example/demo.git", state.OriginUrl);
            Assert.True(state.Refs.Single(r => r is { Kind: RefKind.LocalBranch, Name: "gone-branch" }).UpstreamGone);
            var main = state.Refs.Single(r => r is { Kind: RefKind.LocalBranch, Name: "main" });
            Assert.Equal(("origin/main", 1, 0, false), (main.Upstream, main.Ahead, main.Behind, main.UpstreamGone));
        }
    }

    [Fact]
    public void Status_separates_staged_unstaged_and_untracked()
    {
        _repo.Commit("base", "a.txt", "a");
        _repo.Commit("base2", "b.txt", "b");
        _repo.Write("a.txt", "changed");
        _repo.Write("b.txt", "staged");
        _repo.Git("add", "b.txt");
        _repo.Write("new/c.txt", "new");
        using var session = RepositorySession.Open(_repo.Root);

        var status = session.GetStatus();

        Assert.Contains(status.Unstaged, f => f is { Path: "a.txt", Kind: ChangeKind.Modified });
        Assert.Contains(status.Unstaged, f => f is { Path: "new/c.txt", Kind: ChangeKind.Untracked });
        Assert.Contains(status.Staged, f => f is { Path: "b.txt", Kind: ChangeKind.Modified });
        Assert.DoesNotContain(status.Staged, f => f.Path == "a.txt");
        Assert.Equal(3, status.TotalCount);
    }

    [Fact]
    public void Commit_details_list_changed_files_with_counts()
    {
        _repo.Commit("base", "a.txt", "1\n2\n3\n");
        _repo.Write("a.txt", "1\nTWO\n3\n");
        _repo.Write("b.txt", "new\n");
        _repo.Git("add", "-A");
        _repo.Git("commit", "-q", "-m", "second\n\nbody text");
        var sha = _repo.Git("rev-parse", "HEAD");
        using var session = RepositorySession.Open(_repo.Root);

        var details = session.GetCommitDetails(sha);

        Assert.Equal("second\n\nbody text\n", details.FullMessage);
        Assert.Contains(details.Files, f => f is { Path: "a.txt", Kind: ChangeKind.Modified, Additions: 1, Deletions: 1 });
        Assert.Contains(details.Files, f => f is { Path: "b.txt", Kind: ChangeKind.Added, Additions: 1 });

        var diff = session.GetCommitFileDiff(sha, "a.txt");
        Assert.Contains(diff.Lines, l => l is { Kind: DiffLineKind.Added, Text: "TWO", NewLine: 2 });
        Assert.Contains(diff.Lines, l => l is { Kind: DiffLineKind.Removed, Text: "2", OldLine: 2 });
    }

    [Fact]
    public void Working_diffs_for_staged_unstaged_and_untracked_files()
    {
        _repo.Commit("base", "a.txt", "1\n2\n");
        _repo.Write("a.txt", "1\n2\nstaged\n");
        _repo.Git("add", "a.txt");
        _repo.Write("a.txt", "1\n2\nstaged\nunstaged\n");
        _repo.Write("u.txt", "hello\nworld\n");
        using var session = RepositorySession.Open(_repo.Root);

        var staged = session.GetWorkingFileDiff("a.txt", staged: true);
        var unstaged = session.GetWorkingFileDiff("a.txt", staged: false);
        var untracked = session.GetWorkingFileDiff("u.txt", staged: false);

        Assert.Equal(["staged"], staged.Lines.Where(l => l.Kind == DiffLineKind.Added).Select(l => l.Text));
        Assert.Equal(["unstaged"], unstaged.Lines.Where(l => l.Kind == DiffLineKind.Added).Select(l => l.Text));
        Assert.Equal(["hello", "world"], untracked.Lines.Where(l => l.Kind == DiffLineKind.Added).Select(l => l.Text));
    }

    [Fact]
    public void State_reports_tracking_ahead_and_behind()
    {
        using var remote = new TestRepo(bare: true);
        _repo.Commit("base");
        _repo.Git("remote", "add", "origin", remote.Root);
        _repo.Git("push", "-q", "-u", "origin", "main");
        _repo.Commit("local only");
        using var session = RepositorySession.Open(_repo.Root);

        var state = session.LoadState();

        var main = state.Refs.Single(r => r is { Kind: RefKind.LocalBranch, Name: "main" });
        Assert.Equal("origin/main", main.Upstream);
        Assert.Equal(1, main.Ahead);
        Assert.Equal(0, main.Behind);
        var remoteMain = state.Refs.Single(r => r.Kind == RefKind.RemoteBranch);
        Assert.Equal("origin", remoteMain.RemoteName);
        Assert.Equal("main", remoteMain.ShortName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opens_linked_worktree_and_reports_main_repository(bool relativePaths)
    {
        _repo.Commit("base");
        if (relativePaths) _repo.Git("config", "worktree.useRelativePaths", "true");
        var wtPath = Path.Combine(_repo.Root, ".worktrees", "feat");
        _repo.Git("worktree", "add", "-q", "-b", "feature/feat", wtPath);
        if (relativePaths) Assert.Equal("true", _repo.Git("config", "extensions.relativeworktrees"));

        using var session = RepositorySession.Open(wtPath);
        var state = session.LoadState();

        Assert.True(state.IsLinkedWorktree);
        Assert.Equal("feature/feat", state.CurrentBranch);
        Assert.Equal(Path.GetFileName(_repo.Root), state.RepositoryName);
        Assert.Equal(Path.GetFullPath(_repo.Root), state.MainWorkingDirectory, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(wtPath), state.WorkingDirectory, ignoreCase: true);
        Assert.Single(session.ReadHistory());
        Assert.False(session.GetStatus().IsDirty);
    }
}
