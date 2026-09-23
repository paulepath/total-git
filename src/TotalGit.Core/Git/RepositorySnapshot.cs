namespace TotalGit.Core.Git;

public sealed record RepositorySnapshot(
    string WorkingDirectory,
    string Name,
    string? CurrentBranch,
    string? OriginUrl,
    IReadOnlyList<CommitInfo> Commits,
    IReadOnlyList<RefInfo> Refs);
