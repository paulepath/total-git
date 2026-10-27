using TotalGit.Core.Git;
using TotalGit.Core.Worktrees;

namespace TotalGit.Core.Tests;

public sealed class WorktreeTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public WorktreeTests()
    {
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Theory]
    [InlineData("feature/e4-2330-publish-new-github-repos-via", "e4-2330-publish-new-github-repos-via")]
    [InlineData("main", "main")]
    [InlineData("claude/fix thing!", "fix-thing")]
    [InlineData("a/b/c", "c")]
    [InlineData("feature/", "feature")]
    [InlineData("///", "worktree")]
    public void Suggests_folder_name_from_branch(string branch, string expected) =>
        Assert.Equal(expected, WorktreeService.SuggestName(branch));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lists_main_and_linked_worktrees(bool relativePaths)
    {
        if (relativePaths) _repo.Git("config", "worktree.useRelativePaths", "true");
        var wt = Path.Combine(_repo.Root, ".worktrees", "feat");
        _repo.Git("worktree", "add", "-q", "-b", "feature/feat", wt);
        _repo.Git("worktree", "lock", wt);

        var list = await WorktreeService.ListAsync(wt);

        Assert.Equal(2, list.Count);
        Assert.True(list[0].IsMain);
        Assert.True(WorktreeService.SamePath(_repo.Root, list[0].Path));
        Assert.Equal("main", list[0].Branch);
        Assert.True(WorktreeService.SamePath(wt, list[1].Path));
        Assert.Equal("feature/feat", list[1].Branch);
        Assert.Equal("feat", list[1].Name);
        Assert.True(list[1].IsLocked);
        _repo.Git("worktree", "unlock", wt);
    }

    [Fact]
    public void Parses_prunable_and_detached_entries()
    {
        var list = WorktreeService.ParsePorcelain("""
            worktree D:/r
            HEAD aaa
            branch refs/heads/main

            worktree D:/r/.worktrees/gone
            HEAD bbb
            detached
            prunable gitdir file points to non-existent location

            """);

        Assert.True(list[1].IsDetached);
        Assert.True(list[1].IsPrunable);
        Assert.Equal("gitdir file points to non-existent location", list[1].PrunableReason);
    }

    [Fact]
    public async Task New_repository_has_no_head_commit()
    {
        using var empty = new TestRepo();
        empty.Write("README.md", "hello");

        var main = Assert.Single(await WorktreeService.ListAsync(empty.Root));
        Assert.Null(main.HeadSha); // git says 0000000…, which isn't a commit

        using var session = RepositorySession.Open(empty.Root);
        var state = session.LoadState();
        Assert.Null(state.CurrentBranch);
        Assert.Null(state.HeadSha);
        Assert.Equal(main.Branch, state.UnbornBranch);
        Assert.NotNull(state.UnbornBranch);
    }

    [Fact]
    public void Gitignore_entry_is_added_once()
    {
        _repo.Write(".gitignore", "bin/\r\nobj/");

        Assert.True(WorktreeProvisioner.EnsureGitIgnore(_repo.Root));
        Assert.False(WorktreeProvisioner.EnsureGitIgnore(_repo.Root));

        var text = File.ReadAllText(Path.Combine(_repo.Root, ".gitignore"));
        Assert.Equal(1, text.Split('\n').Count(l => l.Trim() == ".worktrees/"));
        Assert.StartsWith("bin/\r\nobj/\r\n", text);
    }

    [Fact]
    public async Task Creates_worktree_from_new_branch_with_local_files()
    {
        _repo.Write(".gitignore", ".env\nappsettings.Local.json\nbin/\n");
        _repo.Git("add", ".gitignore");
        _repo.Git("commit", "-q", "-m", "ignore");
        _repo.Write(".env", "SECRET=1");
        _repo.Write("src/Api/appsettings.Local.json", "{}");
        _repo.Write("src/Api/bin/appsettings.Local.json", "should not copy");

        var result = await WorktreeProvisioner.CreateAsync(new WorktreeCreateRequest(
            _repo.Root, "e4-1-thing", WorktreeSource.NewBranch, "feature/e4-1-thing", StartPoint: "main"));

        Assert.True(WorktreeService.SamePath(Path.Combine(_repo.Root, ".worktrees", "e4-1-thing"), result.Path));
        Assert.True(result.GitIgnoreUpdated);
        Assert.Equal(["feature/e4-1-thing"], [TestRepo.RunGit(result.Path, "branch", "--show-current")]);
        Assert.Equal("SECRET=1", File.ReadAllText(Path.Combine(result.Path, ".env")));
        Assert.True(File.Exists(Path.Combine(result.Path, "src", "Api", "appsettings.Local.json")));
        Assert.False(File.Exists(Path.Combine(result.Path, "src", "Api", "bin", "appsettings.Local.json")));
        Assert.Equal(2, result.CopiedFiles.Count);
        // The main checkout doesn't see the worktree as untracked content.
        Assert.DoesNotContain(".worktrees", _repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Creates_worktree_for_existing_branch()
    {
        _repo.Git("branch", "feature/existing");

        var result = await WorktreeProvisioner.CreateAsync(new WorktreeCreateRequest(
            _repo.Root, "existing", WorktreeSource.LocalBranch, "feature/existing", CopyLocalFiles: false, LinkNodeModules: false));

        Assert.Equal("feature/existing", TestRepo.RunGit(result.Path, "branch", "--show-current"));
    }

    [Fact]
    public async Task Creates_worktree_tracking_remote_branch()
    {
        using var remote = new TestRepo(bare: true);
        _repo.Git("remote", "add", "origin", remote.Root);
        _repo.Git("push", "-q", "origin", "main:feature/remote-only");
        _repo.Git("fetch", "-q", "origin");

        var result = await WorktreeProvisioner.CreateAsync(new WorktreeCreateRequest(
            _repo.Root, "remote-only", WorktreeSource.RemoteBranch, "feature/remote-only", "origin/feature/remote-only"));

        Assert.Equal("origin/feature/remote-only", TestRepo.RunGit(result.Path, "rev-parse", "--abbrev-ref", "@{u}"));
    }

    [Fact]
    public async Task Refuses_existing_folder()
    {
        Directory.CreateDirectory(Path.Combine(_repo.Root, ".worktrees", "taken"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => WorktreeProvisioner.CreateAsync(
            new WorktreeCreateRequest(_repo.Root, "taken", WorktreeSource.NewBranch, "taken")));
    }

    [Fact]
    public void Copy_skips_existing_files_and_excluded_folders()
    {
        _repo.Write("a/.env", "source");
        _repo.Write("node_modules/pkg/.env", "no");
        _repo.Write(".worktrees/other/.env", "no");
        var dest = Directory.CreateDirectory(Path.Combine(_repo.Root, "..", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dest, "a"));
            File.WriteAllText(Path.Combine(dest, "a", ".env"), "existing");
            var copied = new List<string>();
            var skipped = new List<string>();

            WorktreeProvisioner.CopyLocalFiles(_repo.Root, dest, [".env"], copied, skipped);

            Assert.Empty(copied);
            Assert.Equal([Path.Combine("a", ".env")], skipped);
            Assert.Equal("existing", File.ReadAllText(Path.Combine(dest, "a", ".env")));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public async Task Node_modules_are_linked_and_survive_worktree_removal()
    {
        _repo.Write(".gitignore", "node_modules/\n");
        _repo.Write("web/package.json", "{}");
        _repo.Git("add", "-A");
        _repo.Git("commit", "-q", "-m", "web");
        var sentinel = _repo.Write("web/node_modules/pkg/index.js", "keep me");
        _repo.Write("web/node_modules/pkg/node_modules/nested/x.js", "nested");

        var result = await WorktreeProvisioner.CreateAsync(new WorktreeCreateRequest(
            _repo.Root, "linked", WorktreeSource.NewBranch, "linked"));

        Assert.Equal([Path.Combine("web", "node_modules")], result.LinkedFolders);
        var link = Path.Combine(result.Path, "web", "node_modules");
        Assert.True(WorktreeProvisioner.IsLink(link));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(link, "pkg", "index.js")));

        await WorktreeProvisioner.RemoveAsync(_repo.Root, result.Path);

        Assert.False(Directory.Exists(result.Path));
        Assert.Equal("keep me", File.ReadAllText(sentinel));
        Assert.DoesNotContain("linked", _repo.Git("worktree", "list"));
    }

    [Fact]
    public async Task Remove_reports_a_locked_file_and_can_be_retried_after_it_is_released()
    {
        var result = await WorktreeProvisioner.CreateAsync(new WorktreeCreateRequest(
            _repo.Root, "locked", WorktreeSource.NewBranch, "locked"));
        var file = Path.Combine(result.Path, "file.txt");

        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<WorktreeLockedException>(() =>
                WorktreeProvisioner.RemoveAsync(_repo.Root, result.Path, retryDelay: TimeSpan.FromMilliseconds(50)));
            Assert.Equal(Path.GetFullPath(file), Path.GetFullPath(ex.LockedPath), ignoreCase: true);
        }

        // Git may already have unregistered it; a second attempt must still finish the job.
        await WorktreeProvisioner.RemoveAsync(_repo.Root, result.Path);

        Assert.False(Directory.Exists(result.Path));
        Assert.DoesNotContain("locked", _repo.Git("worktree", "list"));
    }

    [Fact]
    public async Task Remove_finishes_after_git_fails_on_a_locked_ignored_file()
    {
        _repo.Write(".gitignore", "bin/\n");
        _repo.Git("add", ".gitignore");
        _repo.Git("commit", "-q", "-m", "ignore bin");
        var result = await WorktreeProvisioner.CreateAsync(new WorktreeCreateRequest(
            _repo.Root, "build", WorktreeSource.NewBranch, "build"));
        var dll = Path.Combine(result.Path, "bin", "app.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.WriteAllText(dll, "binary");

        using (File.Open(dll, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<WorktreeLockedException>(() =>
                WorktreeProvisioner.RemoveAsync(_repo.Root, result.Path, retryDelay: TimeSpan.FromMilliseconds(50)));
            Assert.Equal(Path.GetFullPath(dll), Path.GetFullPath(ex.LockedPath), ignoreCase: true);
        }

        await WorktreeProvisioner.RemoveAsync(_repo.Root, result.Path);

        Assert.False(Directory.Exists(result.Path));
        Assert.DoesNotContain("build", _repo.Git("worktree", "list"));
    }

    [Fact]
    public async Task Remove_waits_out_a_brief_lock()
    {
        var result = await WorktreeProvisioner.CreateAsync(new WorktreeCreateRequest(
            _repo.Root, "brief", WorktreeSource.NewBranch, "brief"));
        var stream = File.Open(Path.Combine(result.Path, "file.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Delay(400).ContinueWith(_ => stream.Dispose());

        await WorktreeProvisioner.RemoveAsync(_repo.Root, result.Path);

        Assert.False(Directory.Exists(result.Path));
    }

    [Fact]
    public async Task Finds_and_deletes_leftover_folders_without_following_links()
    {
        await WorktreeProvisioner.CreateAsync(new WorktreeCreateRequest(
            _repo.Root, "registered", WorktreeSource.NewBranch, "registered", CopyLocalFiles: false, LinkNodeModules: false));
        var sentinel = _repo.Write("web/node_modules/pkg/index.js", "keep me");
        var leftover = Path.Combine(_repo.Root, ".worktrees", "leftover");
        Directory.CreateDirectory(Path.Combine(leftover, "web"));
        File.WriteAllText(Path.Combine(leftover, "a.txt"), "x");
        File.SetAttributes(Path.Combine(leftover, "a.txt"), FileAttributes.ReadOnly);
        WorktreeProvisioner.LinkNodeModules(_repo.Root, leftover);

        var found = WorktreeService.FindLeftovers(_repo.Root, await WorktreeService.ListAsync(_repo.Root));
        Assert.Equal(["leftover"], found.Select(Path.GetFileName));

        await WorktreeProvisioner.RemoveAsync(_repo.Root, leftover);

        Assert.False(Directory.Exists(leftover));
        Assert.Equal("keep me", File.ReadAllText(sentinel));
        Assert.Empty(WorktreeService.FindLeftovers(_repo.Root, await WorktreeService.ListAsync(_repo.Root)));
    }

    [Fact]
    public async Task Removing_dirty_worktree_requires_force()
    {
        var result = await WorktreeProvisioner.CreateAsync(new WorktreeCreateRequest(
            _repo.Root, "dirty", WorktreeSource.NewBranch, "dirty"));
        File.WriteAllText(Path.Combine(result.Path, "file.txt"), "changed");

        var ex = await Assert.ThrowsAsync<WorktreeDirtyException>(() => WorktreeProvisioner.RemoveAsync(_repo.Root, result.Path));
        Assert.Contains(ex.Changes, c => c.Contains("file.txt"));
        Assert.True(Directory.Exists(result.Path));

        await WorktreeProvisioner.RemoveAsync(_repo.Root, result.Path, force: true);
        Assert.False(Directory.Exists(result.Path));
    }

    [Fact]
    public void Gone_branches_in_linked_worktrees_go_with_them()
    {
        static RefInfo Gone(string name, bool current = false) =>
            new(name, RefKind.LocalBranch, "sha", current, Upstream: $"origin/{name}", UpstreamGone: true);
        static WorktreeInfo Wt(string path, string branch, bool main = false, bool locked = false) =>
            new(path, branch, "sha", main, false, locked, false, null);
        var refs = new[]
        {
            Gone("in-main"), Gone("in-linked", current: true), Gone("in-locked"), Gone("plain"),
            new RefInfo("alive", RefKind.LocalBranch, "sha", false, Upstream: "origin/alive"),
        };
        var worktrees = new[] { Wt("D:/r", "in-main", main: true), Wt("D:/r/.worktrees/a", "in-linked"), Wt("D:/r/.worktrees/b", "in-locked", locked: true) };

        var gone = BranchCleanup.Classify(refs, worktrees).ToDictionary(g => g.Branch.Name);

        Assert.Equal(["in-linked", "in-locked", "in-main", "plain"], gone.Keys);
        Assert.True(gone["in-linked"].CanCleanUp); // even though it's the current branch: you're in that worktree
        Assert.Equal("a", gone["in-linked"].Worktree?.Name);
        Assert.False(gone["in-main"].CanCleanUp);
        Assert.Null(gone["in-main"].Worktree);
        Assert.Contains("locked", gone["in-locked"].BlockedReason);
        Assert.True(gone["plain"].CanCleanUp);
        Assert.Null(gone["plain"].Worktree);

        // Before the worktrees are listed, the current branch is blocked.
        Assert.False(BranchCleanup.Classify(refs, []).Single(g => g.Branch.Name == "in-linked").CanCleanUp);
    }

    [Fact]
    public async Task Removing_a_branchs_worktree_lets_the_branch_be_deleted()
    {
        var result = await WorktreeProvisioner.CreateAsync(new WorktreeCreateRequest(
            _repo.Root, "done", WorktreeSource.NewBranch, "feature/done", CopyLocalFiles: false, LinkNodeModules: false));
        await Assert.ThrowsAsync<BranchInUseException>(() => GitActions.DeleteBranchAsync(_repo.Root, "feature/done", force: true));

        await WorktreeProvisioner.RemoveAsync(_repo.Root, result.Path);
        await GitActions.DeleteBranchAsync(_repo.Root, "feature/done", force: true);

        Assert.False(Directory.Exists(result.Path));
        Assert.Single(await WorktreeService.ListAsync(_repo.Root));
        Assert.Equal("", _repo.Git("branch", "--list", "feature/done"));
    }

    [Fact]
    public async Task Missing_worktrees_are_forgotten_and_leftovers_deleted()
    {
        var missing = await WorktreeProvisioner.CreateAsync(new WorktreeCreateRequest(
            _repo.Root, "missing", WorktreeSource.NewBranch, "missing", CopyLocalFiles: false, LinkNodeModules: false));
        Directory.Delete(missing.Path, recursive: true);
        var leftover = Path.Combine(_repo.Root, ".worktrees", "leftover");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "a.txt"), "x");

        var worktrees = await WorktreeService.ListAsync(_repo.Root);
        Assert.True(worktrees.Single(w => !w.IsMain).IsPrunable);
        foreach (var path in WorktreeService.FindLeftovers(_repo.Root, worktrees)) await WorktreeProvisioner.RemoveAsync(_repo.Root, path);
        await WorktreeProvisioner.PruneAsync(_repo.Root);

        Assert.False(Directory.Exists(leftover));
        Assert.Single(await WorktreeService.ListAsync(_repo.Root));
    }

    [Fact]
    public async Task Removing_a_worktree_whose_folder_has_gone_forgets_it()
    {
        var result = await WorktreeProvisioner.CreateAsync(new WorktreeCreateRequest(
            _repo.Root, "gone", WorktreeSource.NewBranch, "feature/gone", CopyLocalFiles: false, LinkNodeModules: false));
        Directory.Delete(result.Path, recursive: true);

        await WorktreeProvisioner.RemoveAsync(_repo.Root, result.Path);
        await GitActions.DeleteBranchAsync(_repo.Root, "feature/gone", force: true);

        Assert.Single(await WorktreeService.ListAsync(_repo.Root));
    }
}
