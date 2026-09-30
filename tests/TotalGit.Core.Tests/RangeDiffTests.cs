using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class RangeDiffTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Range_from_merge_base_shows_only_the_head_branch_changes()
    {
        _repo.Write("modified.txt", "one\ntwo\nthree\n");
        _repo.Write("deleted.txt", "gone\n");
        _repo.Write("old-name.txt", "a line long enough for rename detection\nanother line\nand a third one\n");
        var root = _repo.Commit("base", "base-only.txt", "base\n");

        _repo.Git("switch", "-q", "-c", "feature");
        _repo.Write("modified.txt", "one\nTWO\nthree\nfour\n");
        _repo.Write("added.txt", "new\n");
        File.Delete(Path.Combine(_repo.Root, "deleted.txt"));
        _repo.Git("mv", "old-name.txt", "new-name.txt");
        var head = _repo.Commit("feature work", "new-name.txt", "a line long enough for rename detection\nanother line\nand a third one\nplus one\n");

        // main moves on after the branch point; a two-dot diff would show this as a deletion.
        _repo.Git("switch", "-q", "main");
        _repo.Commit("main moves on", "base-only.txt", "base\nlater\n");

        using var session = RepositorySession.Open(_repo.Root);
        var mergeBase = session.MergeBase("main", head);
        Assert.Equal(root, mergeBase);

        var files = session.GetRangeChanges(mergeBase!, head);
        Assert.Equal(["added.txt", "deleted.txt", "modified.txt", "new-name.txt"], files.Select(f => f.Path));

        var added = files.Single(f => f.Path == "added.txt");
        Assert.Equal((ChangeKind.Added, 1, 0), (added.Kind, added.Additions, added.Deletions));
        var deleted = files.Single(f => f.Path == "deleted.txt");
        Assert.Equal((ChangeKind.Deleted, 0, 1), (deleted.Kind, deleted.Additions, deleted.Deletions));
        var modified = files.Single(f => f.Path == "modified.txt");
        Assert.Equal((ChangeKind.Modified, 2, 1), (modified.Kind, modified.Additions, modified.Deletions));
        var renamed = files.Single(f => f.Path == "new-name.txt");
        Assert.Equal((ChangeKind.Renamed, "old-name.txt", 1, 0), (renamed.Kind, renamed.OldPath, renamed.Additions, renamed.Deletions));

        var diff = session.GetRangeFileDiff(mergeBase!, head, "modified.txt");
        Assert.False(diff.IsBinary);
        Assert.Equal(["two"], diff.Lines.Where(l => l.Kind == DiffLineKind.Removed).Select(l => l.Text));
        Assert.Equal(["TWO", "four"], diff.Lines.Where(l => l.Kind == DiffLineKind.Added).Select(l => l.Text));

        var renameDiff = session.GetRangeFileDiff(mergeBase!, head, "new-name.txt");
        Assert.DoesNotContain(renameDiff.Lines, l => l.Kind == DiffLineKind.Removed);
        Assert.Equal(["plus one"], renameDiff.Lines.Where(l => l.Kind == DiffLineKind.Added).Select(l => l.Text));

        // The commit view still diffs against the first parent after the range patch was cached.
        Assert.Equal(["base-only.txt"], session.GetCommitDetails(_repo.Git("rev-parse", "main")).Files.Select(f => f.Path));
    }

    [Fact]
    public void Merge_base_is_null_for_unrelated_histories_and_unknown_revisions()
    {
        _repo.Commit("main");
        _repo.Git("switch", "-q", "--orphan", "other");
        _repo.Commit("orphan", "orphan.txt");

        using var session = RepositorySession.Open(_repo.Root);
        Assert.Null(session.MergeBase("main", "other"));
        Assert.Null(session.MergeBase("main", "no-such-branch"));
    }
}
