using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class GitActionsTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task Stage_unstage_and_commit_round_trip()
    {
        _repo.Commit("base", "a.txt", "a");
        _repo.Write("a.txt", "changed");
        _repo.Write("b.txt", "new");

        await GitActions.StageAsync(_repo.Root, ["a.txt", "b.txt"]);
        Assert.Equal(2, _repo.Git("diff", "--cached", "--name-only").Split('\n').Length);

        await GitActions.UnstageAsync(_repo.Root, ["b.txt"]);
        Assert.Equal("a.txt", _repo.Git("diff", "--cached", "--name-only"));

        await GitActions.CommitAsync(_repo.Root, "Summary line\n\nDescription");
        Assert.Equal("Summary line", _repo.Git("log", "-1", "--format=%s"));
        Assert.Equal("Description", _repo.Git("log", "-1", "--format=%b"));
        Assert.Equal("?? b.txt", _repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Creates_lightweight_and_annotated_tags()
    {
        var first = _repo.Commit("one");
        _repo.Commit("two");

        await GitActions.CreateTagAsync(_repo.Root, "v1", first);
        await GitActions.CreateTagAsync(_repo.Root, "v2", "HEAD", "Release two\n\nNotes");

        Assert.Equal("commit", _repo.Git("cat-file", "-t", "v1"));
        Assert.Equal(first, _repo.Git("rev-parse", "v1"));
        Assert.Equal("tag", _repo.Git("cat-file", "-t", "v2"));
        Assert.Equal("Release two", _repo.Git("tag", "-l", "--format=%(contents:subject)", "v2"));

        await GitActions.DeleteTagAsync(_repo.Root, "v1");
        Assert.Equal("v2", _repo.Git("tag", "-l"));
    }

    [Fact]
    public async Task Stash_push_apply_pop_and_drop()
    {
        _repo.Commit("base", "a.txt", "a");
        _repo.Write("a.txt", "first change");
        await GitActions.StashAsync(_repo.Root, "first", includeUntracked: false);
        _repo.Write("b.txt", "untracked");
        await GitActions.StashAsync(_repo.Root, null, includeUntracked: true);
        Assert.Equal("", _repo.Git("status", "--porcelain"));

        using (var session = RepositorySession.Open(_repo.Root))
        {
            var stashes = session.LoadState().Stashes;
            Assert.Equal(2, stashes.Count);
            Assert.Equal((0, "WIP: base", "main"), (stashes[0].Index, stashes[0].Message, stashes[0].Branch));
            Assert.Equal((1, "first", "main"), (stashes[1].Index, stashes[1].Message, stashes[1].Branch));
        }

        await GitActions.StashPopAsync(_repo.Root, 0);
        Assert.Equal("?? b.txt", _repo.Git("status", "--porcelain"));

        await GitActions.StashApplyAsync(_repo.Root, 0);
        Assert.Equal("first change", File.ReadAllText(Path.Combine(_repo.Root, "a.txt")));
        await GitActions.StashDropAsync(_repo.Root, 0);
        Assert.Equal("", _repo.Git("stash", "list"));
    }

    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("release/2026-09", true)]
    [InlineData("has space", false)]
    [InlineData("a..b", false)]
    [InlineData("-x", false)]
    [InlineData("x.lock", false)]
    [InlineData("x/.hidden", false)]
    [InlineData("x~1", false)]
    [InlineData("", false)]
    public void Validates_ref_names(string name, bool valid) => Assert.Equal(valid, GitActions.IsValidRefName(name));

    [Fact]
    public async Task Stage_and_unstage_all_before_first_commit()
    {
        _repo.Write("a.txt", "a");

        await GitActions.StageAllAsync(_repo.Root);
        Assert.Equal("A  a.txt", _repo.Git("status", "--porcelain"));

        await GitActions.UnstageAllAsync(_repo.Root);
        Assert.Equal("?? a.txt", _repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Stages_deleted_files()
    {
        _repo.Commit("base", "a.txt", "a");
        File.Delete(Path.Combine(_repo.Root, "a.txt"));

        await GitActions.StageAsync(_repo.Root, ["a.txt"]);

        Assert.Equal("D  a.txt", _repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Checkout_local_branch()
    {
        _repo.Commit("base");
        _repo.Git("branch", "other");

        await GitActions.CheckoutAsync(_repo.Root, "other");

        Assert.Equal("other", _repo.Git("branch", "--show-current"));
    }

    [Fact]
    public async Task Checkout_branch_used_by_another_worktree_reports_its_path()
    {
        _repo.Commit("base");
        var wt = Path.Combine(_repo.Root, ".worktrees", "other");
        _repo.Git("worktree", "add", "-q", "-b", "other", wt);

        var ex = await Assert.ThrowsAsync<BranchInUseException>(() => GitActions.CheckoutAsync(_repo.Root, "other"));

        Assert.Equal(Path.GetFullPath(wt), Path.GetFullPath(ex.WorktreePath), ignoreCase: true);
    }

    [Fact]
    public async Task Push_sets_upstream_then_pull_and_remote_checkout_work()
    {
        using var remote = new TestRepo(bare: true);
        _repo.Commit("base");
        _repo.Git("remote", "add", "origin", remote.Root);

        await GitActions.PushAsync(_repo.Root);
        Assert.Equal("origin/main", _repo.Git("rev-parse", "--abbrev-ref", "main@{u}"));

        // A second clone pushes a feature branch and a new main commit.
        using var other = new TestRepo(path: Path.Combine(Path.GetTempPath(), "totalgit-tests", Guid.NewGuid().ToString("N")[..12] + "-clone"));
        other.Git("pull", "-q", remote.Root, "main");
        other.Git("remote", "add", "origin", remote.Root);
        other.Commit("from other");
        other.Git("push", "-q", "origin", "main");
        other.Git("push", "-q", "origin", "main:feature/x");

        await GitActions.FetchAsync(_repo.Root);
        await GitActions.PullAsync(_repo.Root);
        Assert.Equal("from other", _repo.Git("log", "-1", "--format=%s"));

        var branch = await GitActions.CheckoutRemoteAsync(_repo.Root, "origin", "feature/x");
        Assert.Equal("feature/x", branch);
        Assert.Equal("feature/x", _repo.Git("branch", "--show-current"));
        Assert.Equal("origin/feature/x", _repo.Git("rev-parse", "--abbrev-ref", "feature/x@{u}"));

        _repo.Commit("on feature");
        await GitActions.PushAsync(_repo.Root);
        Assert.Equal(_repo.Git("rev-parse", "HEAD"), TestRepo.RunGit(remote.Root, "rev-parse", "feature/x"));
    }

    [Fact]
    public async Task Checks_out_a_commit_detached_then_branches_from_it()
    {
        var first = _repo.Commit("one");
        _repo.Commit("two");

        await GitActions.CheckoutDetachedAsync(_repo.Root, first);
        Assert.Equal(first, _repo.Git("rev-parse", "HEAD"));
        Assert.Equal("", _repo.Git("branch", "--show-current"));

        await GitActions.CreateBranchAsync(_repo.Root, "kept", first, checkout: false);
        Assert.Equal("", _repo.Git("branch", "--show-current"));
        await GitActions.CreateBranchAsync(_repo.Root, "fix/old", first, checkout: true);
        Assert.Equal("fix/old", _repo.Git("branch", "--show-current"));
        Assert.Equal(first, _repo.Git("rev-parse", "kept"));
    }

    [Fact]
    public async Task Stages_and_unstages_long_path_lists()
    {
        _repo.Commit("base");
        var paths = Enumerable.Range(0, 120).Select(i => $"many/file {i}.txt").ToArray();
        foreach (var p in paths) _repo.Write(p, p);

        await GitActions.StageAsync(_repo.Root, paths);
        Assert.Equal(120, _repo.Git("diff", "--cached", "--name-only").Split('\n').Length);

        await GitActions.UnstageAsync(_repo.Root, paths);
        Assert.Equal("", _repo.Git("diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task Previews_ignore_rules()
    {
        _repo.Commit("base", "state/tracked.json", "{}");
        _repo.Write("state/a.json", "a");
        _repo.Write("state/a.json.attrs", "a");
        _repo.Write("state/deep/b.json", "b");
        _repo.Write("other.json", "c");

        var folder = await GitActions.PreviewIgnoreAsync(_repo.Root, "/state/");
        Assert.Equal(["state/a.json", "state/a.json.attrs", "state/deep/b.json"], folder.Untracked.Order());
        Assert.Equal(1, folder.TrackedCount);

        var ext = await GitActions.PreviewIgnoreAsync(_repo.Root, "*.attrs\r\n# comment\r\n/other.json");
        Assert.Equal(["other.json", "state/a.json.attrs"], ext.Untracked.Order());
        Assert.Equal(0, ext.TrackedCount);
    }

    [Fact]
    public async Task Escaped_names_match_literally()
    {
        _repo.Commit("base");
        _repo.Write("a[1].txt", "x");
        _repo.Write("a1.txt", "x");
        _repo.Write("#notes", "x");
        _repo.Write("!bang", "x");

        var rules = string.Join('\n', GitIgnore.Escape("a[1].txt"), GitIgnore.Escape("#notes"), GitIgnore.Escape("!bang"));
        var preview = await GitActions.PreviewIgnoreAsync(_repo.Root, rules);

        Assert.Equal(["!bang", "#notes", "a[1].txt"], preview.Untracked.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Adds_ignore_rules_once_after_a_newline()
    {
        _repo.Commit("base", ".gitignore", "bin/");
        _repo.Write("obj/x.dll", "x");

        await GitActions.AddIgnoreRulesAsync(_repo.Root, "/obj/\nbin/", IgnoreTarget.GitIgnore);
        await GitActions.AddIgnoreRulesAsync(_repo.Root, "/obj/", IgnoreTarget.GitIgnore);

        Assert.Equal("bin/\n/obj/\n", File.ReadAllText(Path.Combine(_repo.Root, ".gitignore")));
        Assert.Equal("M .gitignore", _repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Adds_ignore_rules_to_info_exclude_from_a_linked_worktree()
    {
        _repo.Commit("base");
        var wt = Path.Combine(_repo.Root, ".worktrees", "wt");
        _repo.Git("worktree", "add", "-q", "-b", "wt-branch", wt);
        File.WriteAllText(Path.Combine(wt, "local.log"), "x");

        await GitActions.AddIgnoreRulesAsync(wt, "*.log", IgnoreTarget.InfoExclude);

        Assert.Contains("*.log", File.ReadAllText(Path.Combine(_repo.Root, ".git", "info", "exclude")));
        Assert.Equal("", TestRepo.RunGit(wt, "status", "--porcelain"));
    }
}
