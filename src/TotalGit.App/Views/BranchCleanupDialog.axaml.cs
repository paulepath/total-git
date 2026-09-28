using Avalonia.Controls;
using Avalonia.Interactivity;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

public partial class BranchCleanupDialog : Window
{
    public BranchCleanupDialog() => InitializeComponent();

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BranchCleanupViewModel { CanDelete: true }) Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
