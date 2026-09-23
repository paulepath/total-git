namespace TotalGit.Core.Git;

public sealed class RepositoryOpenException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One-shot read of a repository's refs and first page of history.</summary>
public static class RepositoryReader
{
    public const int DefaultMaxCommits = RepositorySession.PageSize;

    /// <summary>Opens the repository containing <paramref name="path"/> and reads its refs and commit history.</summary>
    public static RepositorySnapshot Read(string path, int maxCommits = DefaultMaxCommits)
    {
        using var session = RepositorySession.Open(path);
        var state = session.LoadState();
        var commits = session.ReadHistory(maxCommits);
        return new RepositorySnapshot(state.WorkingDirectory, state.RepositoryName, state.CurrentBranch, state.OriginUrl, commits, state.Refs);
    }
}
