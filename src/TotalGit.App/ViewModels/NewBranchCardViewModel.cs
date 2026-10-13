using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Git;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

/// <summary>
/// The details pane for a branch with no commits yet: says so, which commit it starts from, and what to do next
/// (instead of showing that commit, someone else's, as if it were the branch's).
/// </summary>
public sealed partial class NewBranchCardViewModel(
    NewBranchInfo info,
    CommitInfo? start,
    bool checkedOutHere,
    WorktreeInfo? otherWorktree,
    Action showStart,
    Action? checkout,
    Action? openWorktree)
{
    public string Name => info.Branch.Name;
    public bool IsCheckedOutHere => checkedOutHere;

    public string Explanation => info.BaseName is { } from
        ? $"This branch was made from {from}. Nothing has been committed to it yet, so for now it points at the same commit as {from}."
        : "Nothing has been committed to this branch yet, so for now it points at the commit it was made from.";

    public bool HasStart => start is not null;
    public string StartMessage => start?.MessageShort ?? "";
    public string StartMeta => start is null ? "" : $"{start.AuthorName} · {DateText.Relative(start.AuthorDate)} · {start.ShortSha}";

    /// <summary>What happens next: where his changes will show, or how to start working on it.</summary>
    public string NextText => checkedOutHere
        ? "Nothing changed yet. Files you edit show up on this branch's row at the top of the graph, ready to commit."
        : otherWorktree is not null
            ? $"Checked out in the worktree {otherWorktree.Name}. Changes made there show up on this branch's row."
            : "Not checked out. Check it out to start working on it.";

    public bool CanCheckout => checkout is not null;
    public bool CanOpenWorktree => openWorktree is not null;

    [RelayCommand]
    private void ShowStart() => showStart();

    [RelayCommand]
    private void Checkout() => checkout?.Invoke();

    [RelayCommand]
    private void OpenWorktree() => openWorktree?.Invoke();
}
