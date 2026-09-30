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
        PinButton.IsCheckedChanged += (_, _) => PinToggled?.Invoke(PinButton.IsChecked == true);
        Tree.SelectionChanged += (_, _) =>
        {
            if (Tree.SelectedItem is SidebarNode node) NodeActivated?.Invoke(node);
        };
        Tree.DoubleTapped += (_, e) =>
        {
            if (NodeFrom(e.Source) is not { } node) return;
            // The ↓N and ✕ markers have their own double-click actions instead of checking the branch out.
            if (MarkerAt(e.Source, "behind")) BehindDoubleTapped?.Invoke(node);
            else if (MarkerAt(e.Source, "gone")) GoneDoubleTapped?.Invoke(node);
            else NodeDoubleTapped?.Invoke(node);
            e.Handled = true;
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

    /// <summary>Double-click on a branch's ↓N: bring the remote branch's new commits in.</summary>
    public event Action<SidebarNode>? BehindDoubleTapped;

    /// <summary>Double-click on a branch's ✕ (deleted on the remote).</summary>
    public event Action<SidebarNode>? GoneDoubleTapped;
    public event Action? RefreshPullRequestsRequested;

    public void FocusFilter() => FilterBox.Focus();

    /// <summary>The filter box has the keyboard (the sidebar stays open while it's being typed in).</summary>
    public bool IsFilterFocused => FilterBox.IsFocused;

    /// <summary>Whether the pin shows as pinned; set by the owner, which also saves it.</summary>
    public bool IsPinned
    {
        get => PinButton.IsChecked == true;
        set => PinButton.IsChecked = value;
    }

    /// <summary>The pin was clicked: true to keep the sidebar open, false to fold it away.</summary>
    public event Action<bool>? PinToggled;

    /// <summary>Whether the pointer event came from a marker with <paramref name="cssClass"/> (or something inside it).</summary>
    private static bool MarkerAt(object? source, string cssClass)
    {
        for (var c = source as Control; c is not null and not TreeViewItem; c = c.GetVisualParent() as Control)
            if (c.Classes.Contains(cssClass)) return true;
        return false;
    }

    private static SidebarNode? NodeFrom(object? source) =>
        (source as Control)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as SidebarNode;

    private void OnAddWorktreeClick(object? sender, RoutedEventArgs e)
    {
        AddWorktreeRequested?.Invoke();
        e.Handled = true;
    }

    private void OnRefreshPullRequestsClick(object? sender, RoutedEventArgs e)
    {
        RefreshPullRequestsRequested?.Invoke();
        e.Handled = true;
    }
}
