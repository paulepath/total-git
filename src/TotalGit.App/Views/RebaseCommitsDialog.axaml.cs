using Avalonia.Controls;
using Avalonia.Interactivity;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

public partial class RebaseCommitsDialog : Window
{
    public RebaseCommitsDialog()
    {
        InitializeComponent();
        Opened += (_, _) => TargetBox.Focus();
    }

    private void OnRebase(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RebaseCommitsViewModel { IsValid: true }) Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
