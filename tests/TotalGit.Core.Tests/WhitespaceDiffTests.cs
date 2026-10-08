using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public class WhitespaceDiffTests
{
    private static bool HasChanges(FileDiff diff) => diff.Lines.Any(l => l.Kind is DiffLineKind.Added or DiffLineKind.Removed);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Commit_and_review_range_hide_whitespace_and_cache_does_not_leak_the_option(bool whole)
    {
        using var repo = new TestRepo();
        var start = repo.Commit("base", "a.txt", "first value\nsecond value\n");
        var head = repo.Commit("whitespace", "a.txt", " first   value \n\tsecond value\n");
        using var session = RepositorySession.Open(repo.Root);
        Assert.True(HasChanges(session.GetCommitFileDiff(head, "a.txt", whole)));
        Assert.False(HasChanges(session.GetCommitFileDiff(head, "a.txt", whole, ignoreWhitespace: true)));
        Assert.False(HasChanges(session.GetRangeFileDiff(start, head, "a.txt", whole, ignoreWhitespace: true)));
        Assert.True(HasChanges(session.GetRangeFileDiff(start, head, "a.txt", whole)));
        Assert.True(HasChanges(session.GetCommitFileDiff(head, "a.txt", whole)));
        Assert.Equal(whole, session.GetCommitFileDiff(head, "a.txt", whole, true).IsWholeFile);
        Assert.Single(session.GetRangeChanges(start, head)); // Viewing preference does not hide files from review.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Working_and_staged_whitespace_do_not_appear_as_untracked_additions(bool whole)
    {
        using var repo = new TestRepo();
        repo.Commit("base", "a.txt", "a b\n");
        repo.Write("a.txt", "a   b\n");
        using var session = RepositorySession.Open(repo.Root);
        Assert.True(HasChanges(session.GetWorkingFileDiff("a.txt", false, whole)));
        Assert.False(HasChanges(session.GetWorkingFileDiff("a.txt", false, whole, true)));
        repo.Git("add", "a.txt");
        Assert.False(HasChanges(session.GetWorkingFileDiff("a.txt", false, whole, true)));
        Assert.False(HasChanges(session.GetWorkingFileDiff("a.txt", false, whole)));
        Assert.True(HasChanges(session.GetWorkingFileDiff("a.txt", true, whole)));
        Assert.False(HasChanges(session.GetWorkingFileDiff("a.txt", true, whole, true)));
        repo.Write("new.txt", " untracked contents \n");
        Assert.True(HasChanges(session.GetWorkingFileDiff("new.txt", false, whole, true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Meaningful_changes_keep_line_numbers_and_whole_file_context(bool whole)
    {
        using var repo = new TestRepo();
        var start = repo.Commit("base", "a.txt", " a b\nold value\ncontext\n");
        var head = repo.Commit("mixed", "a.txt", "a   b\nnew value\ncontext\n");
        using var session = RepositorySession.Open(repo.Root);
        var diff = session.GetRangeFileDiff(start, head, "a.txt", whole, true);
        Assert.Equal(2, Assert.Single(diff.Lines, l => l.Kind == DiffLineKind.Added).NewLine);
        Assert.Equal(2, Assert.Single(diff.Lines, l => l.Kind == DiffLineKind.Removed).OldLine);
        Assert.Contains(diff.Lines, l => l.Kind == DiffLineKind.Context && l.Text == "context");
        Assert.Equal(whole, diff.IsWholeFile);
    }

    [Fact]
    public void Root_commits_unborn_staging_and_binary_files_still_show_real_additions()
    {
        using var repo = new TestRepo();
        repo.Write("a.txt", "root content\n");
        repo.Git("add", "a.txt");
        using (var unborn = RepositorySession.Open(repo.Root))
            Assert.True(HasChanges(unborn.GetWorkingFileDiff("a.txt", true, ignoreWhitespace: true)));
        repo.Git("commit", "-m", "root");
        var root = repo.Git("rev-parse", "HEAD");
        using var session = RepositorySession.Open(repo.Root);
        Assert.True(HasChanges(session.GetCommitFileDiff(root, "a.txt", ignoreWhitespace: true)));
        File.WriteAllBytes(repo.Write("binary.bin", ""), [0, 255, 13, 10]);
        repo.Git("add", "binary.bin");
        Assert.True(session.GetWorkingFileDiff("binary.bin", true, ignoreWhitespace: true).IsBinary);
    }

    [Fact]
    public void Renamed_file_compares_both_paths_instead_of_showing_the_whole_file_as_added()
    {
        using var repo = new TestRepo();
        var start = repo.Commit("base", "old.txt", "one\ntwo\nthree\nfour\nfive\n");
        repo.Git("mv", "old.txt", "new.txt");
        repo.Write("new.txt", "one\n two \nthree\nfour\nfive\n");
        repo.Git("add", "-A");
        using (var staging = RepositorySession.Open(repo.Root))
            Assert.False(HasChanges(staging.GetWorkingFileDiff("new.txt", true, ignoreWhitespace: true)));
        repo.Git("commit", "-m", "rename");
        var head = repo.Git("rev-parse", "HEAD");
        using var session = RepositorySession.Open(repo.Root);
        Assert.False(HasChanges(session.GetRangeFileDiff(start, head, "new.txt", ignoreWhitespace: true)));
        Assert.False(HasChanges(session.GetCommitFileDiff(head, "new.txt", ignoreWhitespace: true)));
    }
}
