using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TotalGit.VisualStudio.UI;

/// <summary>One choice in a <see cref="PickDialog"/>.</summary>
public sealed class PickItem
{
    public PickItem(string label, string description, string detail, bool bold = false)
    {
        Label = label;
        Description = description;
        Detail = detail;
        Weight = bold ? FontWeights.Bold : FontWeights.Normal;
    }

    public string Label { get; }
    public string Description { get; }
    public string Detail { get; }
    public FontWeight Weight { get; }
    /// <summary>What the choice stands for (a worktree, a solution path).</summary>
    public object? Tag { get; set; }
}

/// <summary>A filterable list to pick one thing from: a worktree to switch to, or which solution to open.</summary>
public partial class PickDialog
{
    private readonly IReadOnlyList<PickItem> _items;

    public PickDialog(string title, string prompt, IReadOnlyList<PickItem> items, PickItem? preselect, bool offerNewWindow)
    {
        InitializeComponent();
        Title = title;
        Prompt.Text = prompt;
        _items = items;
        NewWindowButton.Visibility = offerNewWindow ? Visibility.Visible : Visibility.Collapsed;
        List.ItemsSource = items;
        List.SelectedItem = preselect ?? items.FirstOrDefault();
        Loaded += (_, _) => Filter.Focus();
    }

    public PickItem? Picked { get; private set; }

    /// <summary>The choice was "Open in New Visual Studio" rather than Open.</summary>
    public bool InNewWindow { get; private set; }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var text = Filter.Text.Trim();
        var shown = text.Length == 0 ? _items : _items.Where(i =>
            i.Label.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
            || i.Description.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
            || i.Detail.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        List.ItemsSource = shown;
        List.SelectedItem = shown.FirstOrDefault();
    }

    /// <summary>Up and down move through the list while typing in the filter.</summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Up) || List.Items.Count == 0) return;
        var index = List.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
        List.SelectedIndex = Math.Max(0, Math.Min(List.Items.Count - 1, index));
        List.ScrollIntoView(List.SelectedItem);
        e.Handled = true;
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => Finish(false);

    private void OnOk(object sender, RoutedEventArgs e) => Finish(false);

    private void OnNewWindow(object sender, RoutedEventArgs e) => Finish(true);

    private void Finish(bool newWindow)
    {
        if (List.SelectedItem is not PickItem item) return;
        Picked = item;
        InNewWindow = newWindow;
        DialogResult = true;
    }
}
