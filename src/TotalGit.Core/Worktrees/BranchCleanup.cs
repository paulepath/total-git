using TotalGit.Core.Git;

namespace TotalGit.Core.Worktrees;

/// <summary>A local branch whose remote branch was deleted: its worktree, if it has one, and why it can't go, if it can't.</summary>
public sealed record GoneBranch(RefInfo Branch, WorktreeInfo? Worktree, string? BlockedReason)
{
    public bool CanCleanUp => BlockedReason is null;
}

/// <summary>Which local branches whose remote branch was deleted can be cleaned up, and with which worktree.</summary>
public static class BranchCleanup
{
    /// <summary>
    /// Every local branch whose upstream is gone, by name. One checked out in a linked worktree goes with its
    /// worktree; one in the main folder (which can't be removed) or in a locked worktree can't be cleaned up.
    /// </summary>
    public static IReadOnlyList<GoneBranch> Classify(IEnumerable<RefInfo> refs, IReadOnlyList<WorktreeInfo> worktrees)
    {
        return refs.Where(r => r is { Kind: RefKind.LocalBranch, UpstreamGone: true })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r =>
            {
                var worktree = worktrees.FirstOrDefault(w => w.Branch == r.Name);
                var reason = worktree switch
                {
                    { IsMain: true } => "Checked out in the main folder: switch it to another branch first.",
                    { IsLocked: true } => $"Its worktree {worktree.Name} is locked.",
                    // Worktrees not listed (yet): the branch is checked out here, wherever here is.
                    null when r.IsCurrent => "The branch you're on: switch to another branch first.",
                    _ => null,
                };
                return new GoneBranch(r, worktree is { IsMain: false } ? worktree : null, reason);
            })
            .ToList();
    }
}
