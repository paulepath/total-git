namespace TotalGit.Core.Git;

public enum RefKind
{
    LocalBranch,
    RemoteBranch,
    Tag,
    DetachedHead,
}

/// <param name="Name">Friendly name, e.g. <c>feature/x</c> or <c>origin/feature/x</c>.</param>
/// <param name="RemoteName">For remote branches, the remote (e.g. <c>origin</c>).</param>
/// <param name="Upstream">For local branches, the tracked remote branch friendly name.</param>
/// <param name="UpstreamGone">For local branches, the configured upstream no longer exists (deleted on the remote and pruned).</param>
public sealed record RefInfo(
    string Name,
    RefKind Kind,
    string TargetSha,
    bool IsCurrent,
    string? RemoteName = null,
    string? Upstream = null,
    int Ahead = 0,
    int Behind = 0,
    bool UpstreamGone = false)
{
    /// <summary>Branch name without the remote prefix (<c>origin/feature/x</c> → <c>feature/x</c>).</summary>
    public string ShortName => RemoteName is not null && Name.StartsWith(RemoteName + "/", StringComparison.Ordinal)
        ? Name[(RemoteName.Length + 1)..]
        : Name;
}
