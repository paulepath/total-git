using Avalonia.Controls;
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
        Files.FileContextRequested += (file, row) =>
        {
            if (DataContext is WorktreeChangesViewModel vm) FileContextRequested?.Invoke(file, vm.Path, row);
        };
    }

    public event Action<WorktreeInfo>? OpenTabRequested;
    public event Action<string>? OpenInCodeRequested;

    /// <summary>Right-click on a file: the file, its worktree folder, and the row.</summary>
    public event Action<FileChangeItem, string, Control>? FileContextRequested;
}
