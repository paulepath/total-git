using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

public partial class StagingView : UserControl
{
    private bool _syncing;
    private StagingViewModel? _vm;

    public StagingView()
    {
        InitializeComponent();
        // One selection across both trees: picking a file in one clears the other. Only user
        // selections count; trees being rebuilt by a status refresh must not clear it.
        UnstagedTree.SelectionChanged += (_, e) => { if (e.AddedItems.Count > 0) OnSelected(UnstagedTree, StagedTree); };
        StagedTree.SelectionChanged += (_, e) => { if (e.AddedItems.Count > 0) OnSelected(StagedTree, UnstagedTree); };

        foreach (var tree in new[] { UnstagedTree, StagedTree })
        {
            tree.ContextRequested += (_, e) =>
            {
                if (NodeAt(e.Source) is { } node && e.Source is Control c)
                {
                    NodeContextRequested?.Invoke(node, c);
                    e.Handled = true;
                }
            };
            tree.DoubleTapped += (_, e) =>
            {
                if (NodeAt(e.Source) is { IsFolder: true } folder) folder.IsExpanded = !folder.IsExpanded;
            };
        }
    }

    /// <summary>Right-click on a file or folder row.</summary>
    public event Action<StagingNode, Control>? NodeContextRequested;

    private static StagingNode? NodeAt(object? source) =>
        (source as Control)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as StagingNode;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.TreesRebuilt -= SyncSelection;
        }
        _vm = DataContext as StagingViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.TreesRebuilt += SyncSelection;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StagingViewModel.SelectedFile)) SyncSelection();
    }

    /// <summary>Shows <see cref="StagingViewModel.SelectedFile"/> as the selected row (the trees hold new nodes after a rebuild).</summary>
    private void SyncSelection()
    {
        if (_syncing || _vm is null) return;
        _syncing = true;
        try
        {
            var selected = _vm.SelectedFile;
            UnstagedTree.SelectedItem = selected is { IsStaged: false } ? Find(_vm.UnstagedTree, selected.Path) : null;
            StagedTree.SelectedItem = selected is { IsStaged: true } ? Find(_vm.StagedTree, selected.Path) : null;
        }
        finally
        {
            _syncing = false;
        }
    }

    private static StagingNode? Find(IEnumerable<StagingNode> nodes, string path)
    {
        foreach (var n in nodes)
        {
            if (n.File?.Path == path) return n;
            if (n.FolderPath is { } folder && path.StartsWith(folder, StringComparison.Ordinal) && Find(n.Children, path) is { } found)
                return found;
        }
        return null;
    }

    private void OnSelected(TreeView source, TreeView other)
    {
        if (_syncing || _vm is null || source.SelectedItem is not StagingNode node) return;
        _syncing = true;
        try
        {
            other.SelectedItem = null;
            // A folder row shows no diff.
            _vm.SelectedFile = node.File;
        }
        finally
        {
            _syncing = false;
        }
    }
}
