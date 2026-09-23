using Avalonia.Controls;
using Avalonia.Interactivity;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

public partial class InteractiveRebaseDialog : Window
{
    public InteractiveRebaseDialog()
    {
        InitializeComponent();
    }

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InteractiveRebaseViewModel { IsValid: true }) Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
