using Avalonia.Controls;
using Avalonia.Interactivity;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

/// <summary>Lists every picture chosen for someone, to change or remove.</summary>
public partial class PeoplePicturesDialog : Window
{
    public PeoplePicturesDialog() => InitializeComponent();

    private async void OnChange(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PeoplePicturesViewModel vm || (sender as Control)?.DataContext is not PeoplePictureRow row) return;
        await new PersonPictureDialog { DataContext = vm.Change(row) }.ShowDialog<bool>(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
