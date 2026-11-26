using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TotalGit.App.Services;

namespace TotalGit.App.ViewModels;

/// <summary>The worktree a branch is checked out in, removed along with the branch.</summary>
/// <param name="IsMissing">Its folder has already gone.</param>
/// <param name="Changes">Uncommitted changes in it, lost when it's removed.</param>
public sealed record BranchCleanupWorktree(string Path, string Name, bool IsMissing, int Changes);

/// <summary>One local branch whose remote branch was deleted (or, with no upstream, whose pull request was merged).</summary>
public sealed partial class BranchCleanupItem : ObservableObject, ICleanupItem
{
    public BranchCleanupItem(string name, string? upstream, string subject, DateTimeOffset? lastCommit, int unpushed, string? blockedReason,
        BranchCleanupWorktree? worktree = null, int? mergedPullRequest = null)
    {
        Name = name;
        Upstream = mergedPullRequest is { } merged ? $"PR #{merged} merged, not on the remote"
            : upstream is null ? "remote branch deleted" : $"{upstream} (deleted)";
        Subject = subject;
        When = lastCommit is { } when ? DateText.Relative(when) : "";
        UnpushedText = unpushed switch
        {
            0 => null,
            1 => "1 commit not on any remote",
            _ => $"{unpushed} commits not on any remote",
        };
        Reason = blockedReason;
        Worktree = blockedReason is null ? worktree : null;
        WorktreeText = Worktree switch
        {
            null => null,
            { IsMissing: true } => $"Its worktree {Worktree.Name} is forgotten too (its folder has already gone).",
            _ => $"Its worktree .worktrees/{Worktree.Name} is removed too.",
        };
        ChangesText = Worktree?.Changes switch
        {
            null or 0 => null,
            1 => "The worktree has 1 uncommitted change, which will be lost.",
            var n => $"The worktree has {n} uncommitted changes, which will be lost.",
        };
        // Losing uncommitted work needs a deliberate tick.
        IsChecked = blockedReason is null && ChangesText is null;
        KindIcon = BranchIcons.ForBranch(name);
    }

    /// <summary>The worktree removed with the branch, or null.</summary>
    public BranchCleanupWorktree? Worktree { get; }
    public string? WorktreeText { get; }
    public bool HasWorktree => WorktreeText is not null;
    public string? ChangesText { get; }
    public bool HasChanges => ChangesText is not null;

    public string Name { get; }
    public string Upstream { get; }
    public string Subject { get; }
    public string When { get; }
    public Bitmap? KindIcon { get; }
    public bool HasKindIcon => KindIcon is not null;

    /// <summary>A hint that deleting could lose work (not proof: squash merges put the work on main as other commits).</summary>
    public string? UnpushedText { get; }
    public bool HasUnpushed => UnpushedText is not null;

    /// <summary>Why the branch can't be deleted (checked out in the main folder, or in a locked worktree), or null.</summary>
    public string? Reason { get; }
    public bool IsSelectable => Reason is null;
    public bool HasReason => Reason is not null;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>The "Clean up branches" dialog: pick which branches with a deleted remote branch to delete.</summary>
public sealed class BranchCleanupViewModel(IEnumerable<BranchCleanupItem> items, bool fetchFailed)
    : CleanupListViewModel<BranchCleanupItem>(items, "branch", "branches")
{
    public bool FetchFailed { get; } = fetchFailed;
}
