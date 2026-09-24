using Avalonia.Controls;
using Avalonia.VisualTree;
using TotalGit.App.ViewModels;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.Views;

public partial class WorktreeChangesView : UserControl
{
    public WorktreeChangesView()
    {
        InitializeComponent();
        OpenTabButton.Click += (_, _) =>
        {
            if (DataContext is WorktreeChangesViewModel vm) OpenTabRequested?.Invoke(vm.Worktree);
        };
        OpenCodeButton.Click += (_, _) =>
        {
            if (DataContext is WorktreeChangesViewModel vm) OpenInCodeRequested?.Invoke(vm.Path);
        };
        FileList.ContextRequested += (_, e) =>
        {
            if (DataContext is WorktreeChangesViewModel vm
                && (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: FileChangeItem file } item)
            {
                FileContextRequested?.Invoke(file, vm.Path, item);
                e.Handled = true;
            }
        };
    }

    public event Action<WorktreeInfo>? OpenTabRequested;
    public event Action<string>? OpenInCodeRequested;

    /// <summary>Right-click on a file: the file, its worktree folder, and the row.</summary>
    public event Action<FileChangeItem, string, Control>? FileContextRequested;
}
