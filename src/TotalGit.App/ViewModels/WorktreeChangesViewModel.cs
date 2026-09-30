using CommunityToolkit.Mvvm.ComponentModel;
using TotalGit.Core.Git;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

/// <summary>
/// The uncommitted changes of a worktree other than the one the tab shows (its WIP row in the graph):
/// read-only, with diffs read through its own session.
/// </summary>
public sealed partial class WorktreeChangesViewModel : ObservableObject, IDisposable
{
    public WorktreeChangesViewModel(WorktreeInfo worktree, RepositorySession session, WorkingTreeStatus status,
        bool showAsTree = true, Action<bool>? showAsTreeChanged = null)
    {
        Worktree = worktree;
        Session = session;
        // Unstaged files first, then staged ones (a file can be in both).
        FileTree = new FileTreeViewModel(
            [.. status.Unstaged.Select(f => new FileChangeItem(f, false)), .. status.Staged.Select(f => new FileChangeItem(f, true))],
            showAsTree, showAsTreeChanged);
        FileTree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileTreeViewModel.SelectedFile)) OnPropertyChanged(nameof(SelectedFile));
        };
    }

    public WorktreeInfo Worktree { get; }
    public RepositorySession Session { get; }
    public string Name => Worktree.Name;
    public string BranchText => Worktree.Branch is { } b ? $"on {b}" : "detached HEAD";
    public string Path => Worktree.Path;

    public FileTreeViewModel FileTree { get; }

    public FileChangeItem? SelectedFile
    {
        get => FileTree.SelectedFile;
        set => FileTree.SelectedFile = value;
    }

    public void Dispose() => Session.Dispose();
}
