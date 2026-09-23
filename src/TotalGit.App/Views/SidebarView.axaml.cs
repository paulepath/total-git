using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
        Tree.SelectionChanged += (_, _) =>
        {
            if (Tree.SelectedItem is SidebarNode node) NodeActivated?.Invoke(node);
        };
        Tree.DoubleTapped += (_, e) =>
        {
            if (NodeFrom(e.Source) is { } node) NodeDoubleTapped?.Invoke(node);
        };
        Tree.ContextRequested += (_, e) =>
        {
            if (NodeFrom(e.Source) is { } node && e.Source is Control c)
            {
                NodeContextRequested?.Invoke(node, c);
                e.Handled = true;
            }
        };
    }

    public event Action<SidebarNode>? NodeActivated;
    public event Action<SidebarNode>? NodeDoubleTapped;
    public event Action<SidebarNode, Control>? NodeContextRequested;
    public event Action? AddWorktreeRequested;

    public void FocusFilter() => FilterBox.Focus();

    private static SidebarNode? NodeFrom(object? source) =>
        (source as Control)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as SidebarNode;

    private void OnAddWorktreeClick(object? sender, RoutedEventArgs e)
    {
        AddWorktreeRequested?.Invoke();
        e.Handled = true;
    }
}
