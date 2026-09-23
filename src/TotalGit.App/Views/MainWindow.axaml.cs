using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using TotalGit.App.ViewModels;
using TotalGit.Core.Git;

namespace TotalGit.App.Views;

public partial class MainWindow : Window, IDialogService
{
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        Graph.AttachScrollBar(GraphScrollBar);
        DiffView.AttachScrollBar(DiffScrollBar);

        Graph.NearEnd += () => _ = _vm?.LoadMoreAsync();
        Graph.CommitContextRequested += (commit, _) =>
        {
            if (_vm is not null) ShowMenu(Graph, _vm.ActionsForCommit(commit));
        };

        Sidebar.NodeActivated += node => _vm?.OnSidebarNodeActivated(node);
        Sidebar.NodeDoubleTapped += node =>
        {
            if (_vm is null) return;
            if (node.IsWorktree && node.Worktree is { } wt) _vm.OpenWorktreeCommand.Execute(wt);
            else if (node.Target is { Kind: RefKind.LocalBranch or RefKind.RemoteBranch } t) _vm.CheckoutCommand.Execute(t);
        };
        Sidebar.NodeContextRequested += (node, control) =>
        {
            if (_vm is not null) ShowMenu(control, _vm.ActionsForSidebar(node));
        };
        Sidebar.AddWorktreeRequested += () => _vm?.CreateWorktreeCommand.Execute(null);
        DetailsView.CopyRequested += sha => _ = CopyToClipboardAsync(sha);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt))
            {
                Sidebar.FocusFilter();
                e.Handled = true;
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.ScrollToShaRequested -= Graph.ScrollToSha;
        _vm = DataContext as MainWindowViewModel;
        if (_vm is null) return;

        _vm.Dialogs = this;
        _vm.ScrollToShaRequested += Graph.ScrollToSha;
        MainGrid.ColumnDefinitions[0].Width = new GridLength(_vm.Settings.SidebarWidth);
        MainGrid.ColumnDefinitions[4].Width = new GridLength(_vm.Settings.DetailsWidth);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.Settings.SidebarWidth = MainGrid.ColumnDefinitions[0].ActualWidth;
            _vm.Settings.DetailsWidth = MainGrid.ColumnDefinitions[4].ActualWidth;
            _vm.Settings.Save();
        }
        base.OnClosing(e);
    }

    private static void ShowMenu(Control target, IReadOnlyList<MenuAction> actions)
    {
        if (actions.Count == 0) return;
        var menu = new ContextMenu
        {
            ItemsSource = actions.Select(ToMenuItem).ToList(),
            Placement = PlacementMode.Pointer,
        };
        menu.Open(target);
    }

    private static object ToMenuItem(MenuAction action) => action.IsSeparator
        ? new Separator()
        : new MenuItem
        {
            Header = action.Header,
            Command = action.Command,
            CommandParameter = action.Parameter,
            IsEnabled = action.IsEnabled,
            ItemsSource = action.Children?.Select(ToMenuItem).ToList(),
        };

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

    public async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public async Task RevealFolderAsync(string path)
    {
        if (Directory.Exists(path)) await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }
}
