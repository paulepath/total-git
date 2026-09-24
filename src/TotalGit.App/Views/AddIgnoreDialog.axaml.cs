using Avalonia.Controls;
using Avalonia.Interactivity;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

public partial class AddIgnoreDialog : Window
{
    public AddIgnoreDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            RulesBox.Focus();
            RulesBox.CaretIndex = RulesBox.Text?.Length ?? 0;
        };
    }

    private void OnIgnore(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AddIgnoreViewModel { CanIgnore: true }) Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
