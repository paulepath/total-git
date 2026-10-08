using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public class HistorySearchTests
{
    [Fact]
    public void Message_body_and_author_search_are_case_insensitive_and_trimmed()
    {
        using var repo = new TestRepo();
        var older = repo.Commit("Subject\n\nDetailed explanation");
        repo.Git("config", "user.name", "Another Author");
        repo.Git("config", "user.email", "another@example.org");
        var newer = repo.Commit("different subject");
        using var session = RepositorySession.Open(repo.Root);
        var loaded = session.ReadHistory();
        Assert.Equal(older, Assert.Single(HistorySearch.Find(loaded, "  EXPLANATION ")).Sha);
        Assert.Equal(newer, Assert.Single(HistorySearch.Find(loaded, "ANOTHER AUTHOR")).Sha);
        Assert.Equal(newer, Assert.Single(HistorySearch.Find(loaded, "@example.ORG")).Sha);
        Assert.Equal(2, HistorySearch.Find(loaded, "subject").Count);
        Assert.Empty(HistorySearch.Find(loaded, "   "));
        Assert.Empty(HistorySearch.Find(loaded, "missing"));
    }

    [Fact]
    public void Sha_matches_prefix_only_and_search_never_loads_more_history()
    {
        using var repo = new TestRepo();
        var older = repo.Commit("older");
        var newer = repo.Commit("newer");
        using var session = RepositorySession.Open(repo.Root);
        var loaded = session.ReadHistory(1);
        Assert.Equal(newer, Assert.Single(HistorySearch.Find(loaded, newer[..8].ToUpperInvariant())).Sha);
        Assert.Empty(HistorySearch.Find(loaded, older));
        Assert.True(session.HasMoreHistory);
        var remaining = session.ReadHistory();
        Assert.Equal(older, Assert.Single(HistorySearch.Find(remaining, older)).Sha);
        var synthetic = new CommitInfo("abcdef012345", [], "", "", DateTimeOffset.Now, "message");
        Assert.Empty(HistorySearch.Find([synthetic], "cdef012"));
        Assert.Empty(HistorySearch.Find([synthetic with { IsWorkingTree = true }], "message"));
    }

    [Theory]
    [InlineData(-1, 0, false, -1)]
    [InlineData(-1, 3, false, 0)]
    [InlineData(-1, 3, true, 2)]
    [InlineData(2, 3, false, 0)]
    [InlineData(0, 3, true, 2)]
    [InlineData(1, 3, false, 2)]
    [InlineData(0, 1, true, 0)]
    public void Navigation_wraps_and_handles_empty_results(int current, int count, bool previous, int expected) =>
        Assert.Equal(expected, HistorySearch.Move(current, count, previous));
}
