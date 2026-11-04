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
/// <param name="IsNew">
/// For local branches, created here and not moved since, and its commit is already on another branch: no commits of
/// its own yet.
/// </param>
/// <param name="CreatedFrom">For a new branch, the branch it was made from (or another branch holding its commit).</param>
/// <param name="MergedPullRequest">
/// For a local branch with no upstream and no remote branch of its name: the merged pull request it was the head of,
/// when the branch hasn't moved since (a branch pushed without -u, merged, then deleted on the remote).
/// </param>
public sealed record RefInfo(
    string Name,
    RefKind Kind,
    string TargetSha,
    bool IsCurrent,
    string? RemoteName = null,
    string? Upstream = null,
    int Ahead = 0,
    int Behind = 0,
    bool UpstreamGone = false,
    bool IsNew = false,
    string? CreatedFrom = null,
    int? MergedPullRequest = null)
{
    /// <summary>For local branches, gone from the remote: its upstream was deleted, or its pull request merged.</summary>
    public bool IsGone => UpstreamGone || MergedPullRequest is not null;

    /// <summary>Branch name without the remote prefix (<c>origin/feature/x</c> → <c>feature/x</c>).</summary>
    public string ShortName => RemoteName is not null && Name.StartsWith(RemoteName + "/", StringComparison.Ordinal)
        ? Name[(RemoteName.Length + 1)..]
        : Name;
}
