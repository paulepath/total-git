using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TotalGit.VisualStudio.UI;

/// <summary>The Worktrees window: one row per worktree of the open solution's repository.</summary>
public partial class WorktreesControl : UserControl
{
    private readonly TotalGitPackage _package;

    internal WorktreesControl(TotalGitPackage package)
    {
        _package = package;
        InitializeComponent();
        Show();
    }

    /// <summary>The worktree selected in the list (what the right-click menu acts on).</summary>
    internal WorktreeEntry? Selected => (List.SelectedItem as WorktreeItem)?.Entry;

    /// <summary>Shows the model's worktrees, keeping the selection.</summary>
    internal void Show()
    {
        var model = _package.Model;
        var selected = Selected?.Folder;
        var items = model.Entries.Select(e => new WorktreeItem(e)).ToList();
        List.ItemsSource = items;
        List.SelectedItem = items.FirstOrDefault(i => selected is not null && i.Entry.Folder == selected);
        MessageText.Text = model.Message ?? "";
        MessageText.Visibility = model.Message is null ? Visibility.Collapsed : Visibility.Visible;
        List.Visibility = model.Message is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        if (ItemUnder(e.OriginalSource) is { } item) _package.SwitchTo(item.Entry);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        if (e.Key == Key.Enter && Selected is { } entry)
        {
            e.Handled = true;
            _package.SwitchTo(entry);
        }
    }

    /// <summary>A right-click selects the row under it first, so the menu acts on that row.</summary>
    private void OnRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource) is { } item) List.SelectedItem = item;
    }

    private void OnRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        if (Selected is null) return;
        e.Handled = true;
        var point = PointToScreen(e.GetPosition(this));
        _package.ShowWorktreeMenu((int)point.X, (int)point.Y);
    }

    private static WorktreeItem? ItemUnder(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem container) return container.DataContext as WorktreeItem;
            if (d is ListBox) return null;
        }
        return null;
    }
}
