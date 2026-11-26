using Avalonia.Controls;
using Avalonia.Interactivity;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

public partial class TagCleanupDialog : Window
{
    public TagCleanupDialog() => InitializeComponent();

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TagCleanupViewModel { CanDelete: true }) Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
