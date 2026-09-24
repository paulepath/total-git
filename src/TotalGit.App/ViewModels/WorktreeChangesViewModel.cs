using CommunityToolkit.Mvvm.ComponentModel;
using TotalGit.Core.Git;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

/// <summary>
/// The uncommitted changes of a worktree other than the one the tab shows (its WIP row in the graph):
/// read-only, with diffs read through its own session.
/// </summary>
public sealed partial class WorktreeChangesViewModel(WorktreeInfo worktree, RepositorySession session, WorkingTreeStatus status)
    : ObservableObject, IDisposable
{
    public WorktreeInfo Worktree { get; } = worktree;
    public RepositorySession Session { get; } = session;
    public string Name => Worktree.Name;
    public string BranchText => Worktree.Branch is { } b ? $"on {b}" : "detached HEAD";
    public string Path => Worktree.Path;

    /// <summary>Unstaged files first, then staged ones (a file can be in both).</summary>
    public IReadOnlyList<FileChangeItem> Files { get; } =
        [.. status.Unstaged.Select(f => new FileChangeItem(f, false)), .. status.Staged.Select(f => new FileChangeItem(f, true))];

    public string FileCountText => status.TotalCount == 1 ? "1 file changed" : $"{status.TotalCount} files changed";

    [ObservableProperty]
    public partial FileChangeItem? SelectedFile { get; set; }

    public void Dispose() => Session.Dispose();
}
