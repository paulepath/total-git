using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using TotalGit.App.Services;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

public partial class MainWindow : Window, IDialogService
{
    private ShellViewModel? _shell;

    public MainWindow()
    {
        InitializeComponent();
        // Our own title row (with a larger icon) only on Windows: macOS puts its window buttons top-left and
        // Linux window managers vary, so there the system title bar stays.
        if (OperatingSystem.IsWindows())
        {
            ExtendClientAreaToDecorationsHint = true;
            Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(HideDrawnTitle, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            TitleRow.IsVisible = false;
        }

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt))
            {
                ActiveView()?.FocusFilter();
                e.Handled = true;
            }
            // Escape closes the diff. Not a key binding: those run before the focused control, and the diff view
            // uses Escape first to clear a text selection.
            else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _shell?.SelectedTab is { } tab)
            {
                tab.CloseDiffCommand.Execute(null);
                e.Handled = true;
            }
        };

        // The mouse's back button leaves the diff (or merge tool), like Escape and a browser's Back. Tunnel, so the
        // diff view and lists under the pointer don't take the press first.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.XButton1Pressed
                && _shell?.SelectedTab is { } tab && (tab.HasDiff || tab.MergeTool is not null))
            {
                tab.CloseDiffCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        // Zoom: Ctrl + wheel and Ctrl +/-/0. Tunnel handlers run before the graph, diff and text boxes see the input.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add:
                    Zoom.ZoomIn();
                    break;
                case Key.OemMinus or Key.Subtract:
                    Zoom.ZoomOut();
                    break;
                case Key.D0 or Key.NumPad0:
                    Zoom.Reset();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;
            if (e.Delta.Y > 0) Zoom.ZoomIn();
            else Zoom.ZoomOut();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Zoom.Changed += () => ApplyZoom(showIndicator: true);
        _zoomIndicatorTimer.Tick += (_, _) =>
        {
            _zoomIndicatorTimer.Stop();
            ZoomIndicator.IsVisible = false;
        };

        // Other worktrees aren't watched: check them when coming back to the app (e.g. after editing in VS Code).
        // Pull requests change on the host: refresh the list too, at most once a minute.
        Activated += (_, _) =>
        {
            RepositoryViewModel.AppIsActive = true;
            if (_shell?.SelectedTab is not { } tab) return;
            _ = tab.RefreshOtherWorktreesAsync();
            _ = tab.RefreshPullRequestsAsync(TimeSpan.FromMinutes(1));
            _ = tab.RefreshWorkflowsAsync();
        };
        // In the background, running workflows are checked once a minute instead of every 15 seconds.
        Deactivated += (_, _) => RepositoryViewModel.AppIsActive = false;

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

    /// <summary>
    /// The window extends into the title bar so it can show a larger app icon (see the title row in
    /// MainWindow.axaml). Avalonia still draws its own title text there, and a full-screen button;
    /// they live outside the styled tree, so hide them here.
    /// </summary>
    private void HideDrawnTitle()
    {
        Visual root = this;
        while (root.GetVisualParent() is { } parent) root = parent;
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            if (control.Name is "PART_TitleTextPanel" or "PART_FullScreenButton" or "PART_PopoverFullScreenButton")
                control.IsVisible = false;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell is not null) _shell.PropertyChanging -= OnShellPropertyChanging;
        if (_shell is not null) _shell.PropertyChanged -= OnShellPropertyChanged;
        _shell = DataContext as ShellViewModel;
        if (_shell is null) return;

        _shell.Dialogs = this;
        Zoom.Initialize(_shell.Settings);
        ApplyZoom(showIndicator: false);
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

    private readonly Avalonia.Threading.DispatcherTimer _zoomIndicatorTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };

    /// <summary>Scales the window content and tooltips to <see cref="Zoom.Level"/>.</summary>
    private void ApplyZoom(bool showIndicator)
    {
        var z = Zoom.Level;
        ZoomHost.LayoutTransform = Math.Abs(z - 1) < 0.001 ? null : new ScaleTransform(z, z);
        // Tooltips are popups outside the scaled content; only they use this theme resource.
        if (Application.Current is { } app) app.Resources["ToolTipContentThemeFontSize"] = 12 * z;

        if (!showIndicator) return;
        ZoomText.Text = $"{Math.Round(z * 100)}%";
        ZoomIndicator.IsVisible = true;
        _zoomIndicatorTimer.Stop();
        _zoomIndicatorTimer.Start();
    }

    /// <summary>Dialogs are separate windows: scale their content (and fixed sizes) to the current zoom.</summary>
    internal static T Zoomed<T>(T dialog) where T : Window
    {
        var z = Zoom.Level;
        if (Math.Abs(z - 1) < 0.001 || dialog.Content is not Control content) return dialog;
        // Detach first: a control can only have one parent.
        dialog.Content = null;
        dialog.Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(z, z), Child = content };
        if (!double.IsNaN(dialog.Width)) dialog.Width *= z;
        if (!double.IsNaN(dialog.Height)) dialog.Height *= z;
        dialog.MinWidth *= z;
        dialog.MinHeight *= z;
        return dialog;
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
        Zoomed(new ConfirmDialog(title, message, details, confirmText)).ShowDialog<bool>(this);

    public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string>? details, IReadOnlyList<DialogChoice> choices) =>
        Zoomed(new ChoiceDialog(title, message, details, choices)).ShowDialog<int?>(this);

    public Task<bool> ShowCreateWorktreeAsync(CreateWorktreeViewModel viewModel) =>
        Zoomed(new CreateWorktreeDialog { DataContext = viewModel }).ShowDialog<bool>(this);

    public Task<bool> ShowFormAsync(FormSpec spec) => Zoomed(new FormDialog(spec)).ShowDialog<bool>(this);

    public Task<bool> ShowInteractiveRebaseAsync(InteractiveRebaseViewModel viewModel) =>
        Zoomed(new InteractiveRebaseDialog { DataContext = viewModel }).ShowDialog<bool>(this);

    public Task<bool> ShowAddIgnoreAsync(AddIgnoreViewModel viewModel) =>
        Zoomed(new AddIgnoreDialog { DataContext = viewModel }).ShowDialog<bool>(this);

    public Task<bool> ShowBranchCleanupAsync(BranchCleanupViewModel viewModel) =>
        Zoomed(new BranchCleanupDialog { DataContext = viewModel }).ShowDialog<bool>(this);

    public Task<bool> ShowRebaseCommitsAsync(RebaseCommitsViewModel viewModel) =>
        Zoomed(new RebaseCommitsDialog { DataContext = viewModel }).ShowDialog<bool>(this);

    public Task<bool> ShowBranchRulesAsync(BranchRulesViewModel viewModel) =>
        Zoomed(new BranchRulesDialog { DataContext = viewModel }).ShowDialog<bool>(this);

    public async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public async Task RevealFolderAsync(string path)
    {
        if (Directory.Exists(path)) await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }
}
