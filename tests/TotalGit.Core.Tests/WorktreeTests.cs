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
}
