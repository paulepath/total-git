using Avalonia.Controls;
using Avalonia.Interactivity;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

public partial class CreateWorktreeDialog : Window
{
    public CreateWorktreeDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            BranchBox.Focus();
            BranchBox.CaretIndex = BranchBox.Text?.Length ?? 0;
        };
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CreateWorktreeViewModel { IsValid: true }) Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
