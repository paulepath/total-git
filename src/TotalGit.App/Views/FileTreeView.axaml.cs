using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

/// <summary>A commit's or worktree's changed files, as a collapsible folder tree or a flat list.</summary>
public partial class FileTreeView : UserControl
{
    private bool _syncing;
    private FileTreeViewModel? _vm;

    public FileTreeView()
    {
        InitializeComponent();
        // Only rows the user picks: the tree losing its selection when the nodes are rebuilt doesn't count.
        Tree.SelectionChanged += (_, e) =>
        {
            if (_syncing || _vm is null || e.AddedItems.Count == 0) return;
            // A folder row shows no diff.
            _vm.SelectedFile = (Tree.SelectedItem as StagingNode)?.File;
        };
        Tree.ContextRequested += (_, e) =>
        {
            if (RowAt(e.Source) is { DataContext: StagingNode { File: { } file } } row)
            {
                FileContextRequested?.Invoke(file, row);
                e.Handled = true;
            }
        };
        Tree.DoubleTapped += (_, e) =>
        {
            if (RowAt(e.Source)?.DataContext is StagingNode { IsFolder: true } folder) folder.IsExpanded = !folder.IsExpanded;
        };
    }

    /// <summary>Right-click on a file row.</summary>
    public event Action<FileChangeItem, Control>? FileContextRequested;

    private static TreeViewItem? RowAt(object? source) => (source as Control)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as FileTreeViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        SyncSelection();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The nodes are rebuilt when switching between tree and list; the selected file is kept.
        if (e.PropertyName == nameof(FileTreeViewModel.SelectedFile)) SyncSelection();
        // After the tree has taken the new nodes.
        else if (e.PropertyName == nameof(FileTreeViewModel.Nodes)) Dispatcher.UIThread.Post(SyncSelection);
    }

    private void SyncSelection()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            Tree.SelectedItem = _vm?.NodeFor(_vm.SelectedFile);
        }
        finally
        {
            _syncing = false;
        }
    }
}
