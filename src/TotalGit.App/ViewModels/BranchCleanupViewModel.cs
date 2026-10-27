using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TotalGit.App.Services;

namespace TotalGit.App.ViewModels;

/// <summary>The worktree a branch is checked out in, removed along with the branch.</summary>
/// <param name="IsMissing">Its folder has already gone.</param>
/// <param name="Changes">Uncommitted changes in it, lost when it's removed.</param>
public sealed record BranchCleanupWorktree(string Path, string Name, bool IsMissing, int Changes);

/// <summary>One local branch whose remote branch was deleted.</summary>
public sealed partial class BranchCleanupItem : ObservableObject
{
    public BranchCleanupItem(string name, string? upstream, string subject, DateTimeOffset? lastCommit, int unpushed, string? blockedReason,
        BranchCleanupWorktree? worktree = null)
    {
        Name = name;
        Upstream = upstream is null ? "remote branch deleted" : $"{upstream} (deleted)";
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
public sealed partial class BranchCleanupViewModel : ObservableObject
{
    private bool _updatingAll;

    public BranchCleanupViewModel(IEnumerable<BranchCleanupItem> items, bool fetchFailed)
    {
        Items = new ObservableCollection<BranchCleanupItem>(items);
        FetchFailed = fetchFailed;
        foreach (var item in Items) item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BranchCleanupItem.IsChecked)) OnSelectionChanged();
        };
        OnSelectionChanged();
    }

    public ObservableCollection<BranchCleanupItem> Items { get; }

    public bool FetchFailed { get; }

    public IReadOnlyList<BranchCleanupItem> Selected => Items.Where(i => i.IsSelectable && i.IsChecked).ToList();

    private int SelectableCount => Items.Count(i => i.IsSelectable);

    public string SelectionText => $"{Selected.Count} of {SelectableCount} selected";

    public bool CanDelete => Selected.Count > 0;

    public string DeleteText => Selected.Count switch
    {
        0 => "Delete",
        1 => "Delete 1 branch",
        var n => $"Delete {n} branches",
    };

    /// <summary>Select all: checks or unchecks every deletable branch; shows "mixed" (null) for a partial selection.</summary>
    [ObservableProperty]
    public partial bool? AllChecked { get; set; }

    partial void OnAllCheckedChanged(bool? oldValue, bool? newValue)
    {
        if (_updatingAll) return;
        // Clicking a mixed box selects all (the checkbox itself would go to "none").
        var check = oldValue is null || newValue != false;
        _updatingAll = true;
        foreach (var item in Items.Where(i => i.IsSelectable)) item.IsChecked = check;
        _updatingAll = false;
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        if (_updatingAll) return;
        var selected = Selected.Count;
        _updatingAll = true;
        AllChecked = selected == 0 ? false : selected == SelectableCount ? true : null;
        _updatingAll = false;
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(DeleteText));
    }
}
