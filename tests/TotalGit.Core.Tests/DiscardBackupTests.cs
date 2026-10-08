using System.Text.Json;
using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public class DiscardBackupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_restores_index_and_working_bytes_without_touching_other_files(bool staged)
    {
        using var repo = new TestRepo();
        repo.Commit("base", "a.txt", "base");
        repo.Commit("other", "b.txt", "base");
        repo.Write("a.txt", "index");
        repo.Git("add", "a.txt");
        repo.Write("a.txt", "working");
        var backup = staged
            ? await GitActions.DiscardStagedAsync(repo.Root, [new("a.txt", null, ChangeKind.Modified)])
            : await GitActions.DiscardUnstagedAsync(repo.Root, [new("a.txt", null, ChangeKind.Modified)]);
        repo.Write("b.txt", "new edit after discard");
        repo.Git("add", "b.txt");
        await backup!.UndoAsync(repo.Root);
        Assert.Equal("working", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Equal("index", repo.Git("show", ":a.txt"));
        Assert.Equal("new edit after discard", repo.Git("show", ":b.txt"));
    }

    [Fact]
    public async Task Undo_restores_binary_untracked_and_staged_additions_in_an_unborn_repository()
    {
        using var repo = new TestRepo();
        var bytes = new byte[] { 0, 255, 128, 1, 10, 13, 0 };
        var file = repo.Write("new.bin", "");
        File.WriteAllBytes(file, bytes);
        repo.Git("add", "new.bin");
        repo.Write("[scratch].txt", "never committed");
        var backup = await GitActions.DiscardAllAsync(repo.Root, includeUntracked: true);
        Assert.False(File.Exists(file));
        // Recovery also survives losing the original loose blob: the index bytes are in the backup.
        var sha = backup!.Paths.Single(p => p.Path == "new.bin").Index.Single().Sha;
        var blob = Path.Combine(repo.Root, ".git", "objects", sha[..2], sha[2..]);
        File.SetAttributes(blob, FileAttributes.Normal);
        File.Delete(blob);
        await backup.UndoAsync(repo.Root);
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal("never committed", File.ReadAllText(Path.Combine(repo.Root, "[scratch].txt")));
        Assert.Contains("A  new.bin", repo.Git("status", "--porcelain"));
        Assert.DoesNotContain("scratch", repo.Git("ls-files"));
    }

    [Fact]
    public async Task Undo_rename_and_deleted_working_file()
    {
        using var repo = new TestRepo();
        repo.Commit("base", "old.txt", "base");
        repo.Git("mv", "old.txt", "new.txt");
        File.Delete(Path.Combine(repo.Root, "new.txt"));
        var status = repo.Git("status", "--porcelain");
        var backup = await GitActions.DiscardStagedAsync(repo.Root, [new("new.txt", "old.txt", ChangeKind.Renamed)]);
        await backup!.UndoAsync(repo.Root);
        Assert.Equal(status, repo.Git("status", "--porcelain"));
        Assert.False(File.Exists(Path.Combine(repo.Root, "old.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "new.txt")));
        Assert.Equal("base", repo.Git("show", ":new.txt"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_refuses_newer_working_or_index_changes(bool staged)
    {
        using var repo = new TestRepo();
        repo.Commit("base");
        repo.Write("file.txt", "discard me");
        var backup = await GitActions.DiscardUnstagedAsync(repo.Root, [new("file.txt", null, ChangeKind.Modified)]);
        repo.Write("file.txt", "newer");
        if (staged)
        {
            repo.Git("add", "file.txt");
            repo.Write("file.txt", "base");
        }
        var before = repo.Git("status", "--porcelain");
        await Assert.ThrowsAsync<InvalidOperationException>(() => backup!.UndoAsync(repo.Root));
        Assert.Equal(before, repo.Git("status", "--porcelain"));
        Assert.Equal(staged ? "base" : "newer", File.ReadAllText(Path.Combine(repo.Root, "file.txt")));
    }

    [Fact]
    public async Task Untracked_literal_paths_do_not_delete_other_files_and_backup_survives_reloading()
    {
        using var repo = new TestRepo();
        repo.Write("[a].txt", "wanted");
        repo.Write("a.txt", "unrelated");
        await GitActions.DiscardUnstagedAsync(repo.Root, [new("[a].txt", null, ChangeKind.Untracked)]);
        Assert.True(File.Exists(Path.Combine(repo.Root, "a.txt")));
        var backup = await DiscardBackup.LatestAsync(repo.Root);
        await backup!.UndoAsync(repo.Root);
        Assert.Equal("wanted", File.ReadAllText(Path.Combine(repo.Root, "[a].txt")));
        Assert.Null(await DiscardBackup.LatestAsync(repo.Root));
    }

    [Fact]
    public async Task Backup_failure_prevents_deleting_any_files()
    {
        using var repo = new TestRepo();
        repo.Commit("base");
        repo.Write("file.txt", "keep me");
        File.WriteAllText(Path.Combine(repo.Root, ".git", "totalgit"), "blocks backup folder");
        await Assert.ThrowsAnyAsync<IOException>(() => GitActions.DiscardAllAsync(repo.Root, true));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(repo.Root, "file.txt")));
    }

    [Fact]
    public async Task Discard_all_undo_does_not_restore_or_block_on_untracked_files_that_were_kept()
    {
        using var repo = new TestRepo();
        repo.Commit("base");
        repo.Write("file.txt", "discarded");
        repo.Write("kept.txt", "untracked");
        var backup = await GitActions.DiscardAllAsync(repo.Root, false);
        repo.Write("kept.txt", "newer unrelated edit");
        await backup!.UndoAsync(repo.Root);
        Assert.Equal("discarded", File.ReadAllText(Path.Combine(repo.Root, "file.txt")));
        Assert.Equal("newer unrelated edit", File.ReadAllText(Path.Combine(repo.Root, "kept.txt")));
    }

    [Fact]
    public async Task Linked_worktree_backups_are_isolated_from_the_main_worktree()
    {
        using var repo = new TestRepo();
        repo.Commit("base");
        var linked = Path.Combine(repo.Root, ".worktrees", "other");
        repo.Git("worktree", "add", "-b", "other", linked);
        File.WriteAllText(Path.Combine(linked, "file.txt"), "linked edit");
        await GitActions.DiscardAllAsync(linked, false);
        Assert.Null(await DiscardBackup.LatestAsync(repo.Root));
        await (await DiscardBackup.LatestAsync(linked))!.UndoAsync(linked);
        Assert.Equal("linked edit", File.ReadAllText(Path.Combine(linked, "file.txt")));
        Assert.Equal("base", File.ReadAllText(Path.Combine(repo.Root, "file.txt")));
    }

    [Fact]
    public void Retention_prunes_by_age_and_count()
    {
        using var repo = new TestRepo();
        var root = Path.Combine(repo.Root, ".git", "totalgit", "discarded");
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 24; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            var folder = Path.Combine(root, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "backup.json"), JsonSerializer.Serialize(new DiscardBackup { Id = id, Created = now.AddMinutes(-i) }));
        }
        DiscardBackup.Prune(root, now);
        Assert.Equal(DiscardBackup.KeepCount, Directory.GetDirectories(root).Length);
        DiscardBackup.Prune(root, now.AddDays(8));
        Assert.Empty(Directory.GetDirectories(root));
    }

    [Fact]
    public async Task Checkout_discard_and_hard_reset_also_keep_backups()
    {
        using var repo = new TestRepo();
        var first = repo.Commit("first", "file.txt", "first");
        repo.Git("branch", "other");
        repo.Commit("second", "file.txt", "second");
        repo.Write("file.txt", "lost at checkout");
        await GitActions.CheckoutAsync(repo.Root, "other", LocalChanges.Discard);
        await (await DiscardBackup.LatestAsync(repo.Root))!.UndoAsync(repo.Root);
        Assert.Equal("lost at checkout", File.ReadAllText(Path.Combine(repo.Root, "file.txt")));
        await GitActions.ResetAsync(repo.Root, first, ResetMode.Hard);
        await (await DiscardBackup.LatestAsync(repo.Root))!.UndoAsync(repo.Root);
        Assert.Equal("lost at checkout", File.ReadAllText(Path.Combine(repo.Root, "file.txt")));
    }

    [Fact]
    public async Task Discarding_everything_backs_up_only_the_changed_files()
    {
        using var repo = new TestRepo();
        for (var i = 0; i < 30; i++) repo.Write($"src/f{i}.txt", $"file {i}");
        repo.Commit("base", "readme.md", "base");
        repo.Write("src/f3.txt", "edited");
        repo.Write("notes.txt", "untracked");

        var tracked = await GitActions.DiscardAllAsync(repo.Root, includeUntracked: false);

        Assert.Equal(["src/f3.txt"], tracked!.Paths.Select(p => p.Path));
        Assert.True(File.Exists(Path.Combine(repo.Root, "notes.txt")));
        var all = await GitActions.DiscardAllAsync(repo.Root, includeUntracked: true);
        Assert.Equal(["notes.txt"], all!.Paths.Select(p => p.Path));
    }

    [Fact]
    public async Task A_repository_with_a_submodule_can_still_discard_and_reset()
    {
        using var lib = new TestRepo();
        lib.Commit("lib", "lib.txt", "lib");
        using var repo = new TestRepo();
        repo.Commit("base", "a.txt", "a");
        repo.Git("-c", "protocol.file.allow=always", "submodule", "add", "-q", lib.Root, "lib");
        var head = repo.Commit("add submodule", "b.txt", "b");

        repo.Write("a.txt", "changed");
        Assert.NotNull(await GitActions.DiscardAllAsync(repo.Root, includeUntracked: true));
        Assert.Equal("a", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));

        repo.Write("a.txt", "changed again");
        await GitActions.ResetAsync(repo.Root, head, ResetMode.Hard);
        await (await DiscardBackup.LatestAsync(repo.Root))!.UndoAsync(repo.Root);
        Assert.Equal("changed again", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
    }
}
