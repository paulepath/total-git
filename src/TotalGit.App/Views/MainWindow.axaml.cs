using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

public partial class MainWindow : Window, IDialogService
{
    private ShellViewModel? _shell;

    public MainWindow()
    {
        InitializeComponent();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt))
            {
                ActiveView()?.FocusFilter();
                e.Handled = true;
            }
        };

        // Middle-click closes a tab, as in browsers.
        TabStrip.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Middle
                && (e.Source as Control)?.DataContext is RepositoryViewModel tab)
            {
                _shell?.CloseTabCommand.Execute(tab);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell is not null) _shell.PropertyChanging -= OnShellPropertyChanging;
        if (_shell is not null) _shell.PropertyChanged -= OnShellPropertyChanged;
        _shell = DataContext as ShellViewModel;
        if (_shell is null) return;

        _shell.Dialogs = this;
        _shell.PropertyChanging += OnShellPropertyChanging;
        _shell.PropertyChanged += OnShellPropertyChanged;
    }

    // Pane and column widths are shared: carry them from the tab being left to the tab being shown.
    private void OnShellPropertyChanging(object? sender, System.ComponentModel.PropertyChangingEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SelectedTab) && _shell is not null)
            ActiveView()?.SaveLayout(_shell.Settings);
    }

    private void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SelectedTab) && _shell is not null)
            ActiveView()?.ApplyLayout(_shell.Settings);
    }

    private RepositoryView? ActiveView() => _shell?.SelectedTab is { } tab
        ? TabViews.GetVisualDescendants().OfType<RepositoryView>().FirstOrDefault(v => v.DataContext == tab)
        : null;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_shell is not null)
        {
            ActiveView()?.SaveLayout(_shell.Settings);
            _shell.Settings.Save();
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ IDialogService

    public async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open git repository",
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public Task<bool> ConfirmAsync(string title, string message, IReadOnlyList<string>? details = null, string confirmText = "OK") =>
        new ConfirmDialog(title, message, details, confirmText).ShowDialog<bool>(this);

    public Task<bool> ShowCreateWorktreeAsync(CreateWorktreeViewModel viewModel) =>
        new CreateWorktreeDialog { DataContext = viewModel }.ShowDialog<bool>(this);

    public Task<bool> ShowFormAsync(FormSpec spec) => new FormDialog(spec).ShowDialog<bool>(this);

    public Task<bool> ShowInteractiveRebaseAsync(InteractiveRebaseViewModel viewModel) =>
        new InteractiveRebaseDialog { DataContext = viewModel }.ShowDialog<bool>(this);

    public async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public async Task RevealFolderAsync(string path)
    {
        if (Directory.Exists(path)) await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }
}
