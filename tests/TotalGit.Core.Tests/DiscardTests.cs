using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public class DiscardTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    private string Read(string path) => File.ReadAllText(Path.Combine(_repo.Root, path));
    private bool Exists(string path) => File.Exists(Path.Combine(_repo.Root, path));
    private string Status() => _repo.Git("status", "--porcelain");

    [Fact]
    public async Task Unstaged_discard_keeps_what_is_staged()
    {
        _repo.Commit("one", "a.txt", "v1");
        _repo.Commit("two", "b.txt", "keep");
        _repo.Write("a.txt", "v2");
        _repo.Git("add", "a.txt");
        _repo.Write("a.txt", "v3");
        _repo.Write("b.txt", "changed too");

        await GitActions.DiscardUnstagedAsync(_repo.Root, [new FileChange("a.txt", null, ChangeKind.Modified)]);

        Assert.Equal("v2", Read("a.txt"));
        Assert.Equal("changed too", Read("b.txt"));
        Assert.Contains("M  a.txt", Status());
    }

    [Fact]
    public async Task Unstaged_discard_restores_deleted_files_and_deletes_untracked_ones()
    {
        _repo.Commit("one", "a.txt", "v1");
        File.Delete(Path.Combine(_repo.Root, "a.txt"));
        _repo.Write("new/scratch.txt", "junk");
        _repo.Write("other.txt", "kept");

        await GitActions.DiscardUnstagedAsync(_repo.Root,
            [new FileChange("a.txt", null, ChangeKind.Deleted), new FileChange("new/scratch.txt", null, ChangeKind.Untracked)]);

        Assert.Equal("v1", Read("a.txt"));
        Assert.False(Exists("new/scratch.txt"));
        Assert.True(Exists("other.txt"));
    }

    [Fact]
    public async Task Conflicted_files_are_left_alone()
    {
        _repo.Commit("one", "a.txt", "v1");
        _repo.Write("a.txt", "v2");

        await GitActions.DiscardUnstagedAsync(_repo.Root, [new FileChange("a.txt", null, ChangeKind.Conflicted)]);
        await GitActions.DiscardStagedAsync(_repo.Root, [new FileChange("a.txt", null, ChangeKind.Conflicted)]);

        Assert.Equal("v2", Read("a.txt"));
    }

    [Fact]
    public async Task Staged_discard_goes_back_to_head()
    {
        _repo.Commit("one", "a.txt", "v1");
        _repo.Write("a.txt", "v2");
        _repo.Git("add", "a.txt");
        _repo.Write("a.txt", "v3");
        _repo.Write("added.txt", "new");
        _repo.Git("add", "added.txt");

        await GitActions.DiscardStagedAsync(_repo.Root,
            [new FileChange("a.txt", null, ChangeKind.Modified), new FileChange("added.txt", null, ChangeKind.Added)]);

        Assert.Equal("v1", Read("a.txt"));
        Assert.False(Exists("added.txt"));
        Assert.Equal("", Status());
    }

    [Fact]
    public async Task Staged_discard_of_a_rename_brings_the_old_name_back()
    {
        _repo.Commit("one", "old.txt", "content");
        _repo.Git("mv", "old.txt", "new.txt");

        await GitActions.DiscardStagedAsync(_repo.Root, [new FileChange("new.txt", "old.txt", ChangeKind.Renamed)]);

        Assert.True(Exists("old.txt"));
        Assert.False(Exists("new.txt"));
        Assert.Equal("", Status());
    }

    [Fact]
    public async Task Staged_discard_works_before_the_first_commit()
    {
        _repo.Write("a.txt", "new");
        _repo.Git("add", "a.txt");

        await GitActions.DiscardStagedAsync(_repo.Root, [new FileChange("a.txt", null, ChangeKind.Added)]);

        Assert.False(Exists("a.txt"));
        Assert.Equal("", Status());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discard_all_deletes_untracked_files_only_when_asked(bool includeUntracked)
    {
        _repo.Commit("one", "a.txt", "v1");
        _repo.Write("a.txt", "v2");
        _repo.Git("add", "a.txt");
        _repo.Commit("two", "b.txt", "b1");
        _repo.Write("b.txt", "b2");
        _repo.Write("dir/untracked.txt", "u");

        await GitActions.DiscardAllAsync(_repo.Root, includeUntracked);

        Assert.Equal("v2", Read("a.txt")); // committed by "two" along with b.txt
        Assert.Equal("b1", Read("b.txt"));
        Assert.Equal(!includeUntracked, Exists("dir/untracked.txt"));
    }

    [Fact]
    public async Task Long_lists_are_handled()
    {
        for (var i = 0; i < 60; i++) _repo.Write($"t{i}.txt", "v1");
        _repo.Git("add", "-A");
        _repo.Git("commit", "-q", "-m", "many");
        var tracked = new List<FileChange>();
        for (var i = 0; i < 60; i++)
        {
            _repo.Write($"t{i}.txt", "v2");
            _repo.Write($"u{i}.txt", "junk");
            tracked.Add(new FileChange($"t{i}.txt", null, ChangeKind.Modified));
            tracked.Add(new FileChange($"u{i}.txt", null, ChangeKind.Untracked));
        }

        await GitActions.DiscardUnstagedAsync(_repo.Root, tracked);

        Assert.Equal("", Status());
    }
}
