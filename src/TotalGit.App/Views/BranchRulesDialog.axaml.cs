using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

public partial class BranchRulesDialog : Window
{
    public BranchRulesDialog() => InitializeComponent();

    private async void OnUpload(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BranchRulesViewModel vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an icon",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.ico"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.AddCustomIcon(path);
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
