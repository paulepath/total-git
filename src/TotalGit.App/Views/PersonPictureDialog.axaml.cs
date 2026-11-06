using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

/// <summary>Chooses a picture for someone; closes with true once it's saved or removed.</summary>
public partial class PersonPictureDialog : Window
{
    public PersonPictureDialog()
    {
        InitializeComponent();
        DropTarget.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropTarget.AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropTarget.Classes.Set("dragging", false));
        DropTarget.AddHandler(DragDrop.DropEvent, OnDrop);
        NewAddressBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Vm is not { } vm) return;
            vm.AddAddressCommand.Execute(null);
            e.Handled = true;
        };
    }

    private PersonPictureViewModel? Vm => DataContext as PersonPictureViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is { } vm) vm.PropertyChanged += OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PersonPictureViewModel.Saved) && Vm?.Saved == true) Close(true);
    }

    private async void OnChoose(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Choose a picture for {vm.Name}",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.UseFile(path);
    }

    private async void OnPaste(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Clipboard is not { } clipboard) return;
        try
        {
            if (await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                using (bitmap) vm.UseBitmap(bitmap);
                return;
            }
            if (await clipboard.TryGetFileAsync() is { } file && file.TryGetLocalPath() is { } path)
            {
                vm.UseFile(path);
                return;
            }
            vm.ShowError("There's no picture on the clipboard. Copy one first (or copy a picture file).");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            vm.ShowError("Couldn't read the clipboard: " + ex.Message);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var ok = e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Bitmap);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropTarget.Classes.Set("dragging", ok);
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropTarget.Classes.Set("dragging", false);
        if (Vm is not { } vm) return;
        if (e.DataTransfer.TryGetFile() is { } file && file.TryGetLocalPath() is { } path) vm.UseFile(path);
        else if (e.DataTransfer.TryGetBitmap() is { } bitmap)
            using (bitmap) vm.UseBitmap(bitmap);
        e.Handled = true;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
