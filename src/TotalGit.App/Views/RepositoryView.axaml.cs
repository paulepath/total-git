using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.App.ViewModels;
using TotalGit.Core.Git;

namespace TotalGit.App.Views;

/// <summary>The contents of one repository tab. Each tab keeps its own view, so scroll and selection survive switching.</summary>
public partial class RepositoryView : UserControl
{
    private RepositoryViewModel? _vm;

    public RepositoryView()
    {
        InitializeComponent();
        SetUpSidebarFolding();
        Graph.AttachScrollBar(GraphScrollBar);
        DiffView.AttachScrollBar(DiffScrollBar);
        DiffView.AttachHorizontalScrollBar(DiffHScrollBar);

        Graph.NearEnd += () => _ = _vm?.LoadMoreAsync();
        Graph.ColumnsChanged += () => SaveGraphColumns(_vm?.Settings);
        Graph.CommitContextRequested += (commit, _) =>
        {
            if (_vm is not null) ShowMenu(Graph, _vm.ActionsForCommit(commit));
        };
        Graph.RefContextRequested += r =>
        {
            if (_vm is not null) ShowMenu(Graph, _vm.ActionsForRef(r));
        };
        Graph.RefActivated += r => _vm?.ActivateRef(r);
        Graph.RangeSelectRequested += (anchor, other) => _vm?.SelectRange(anchor, other);
        Graph.RangeDropped += target => _vm?.RebaseRangeCommand.Execute(target);
        Graph.ExpandFoldRequested += sha => _vm?.ExpandFoldCommand.Execute(sha);

        Sidebar.NodeActivated += node => _vm?.OnSidebarNodeActivated(node);
        BannerBar.PointerEntered += (_, _) => _vm?.PauseBannerTimer(true);
        BannerBar.PointerExited += (_, _) => _vm?.PauseBannerTimer(false);
        Sidebar.BehindDoubleTapped += node =>
        {
            if (node.Target is { Kind: RefKind.LocalBranch } t) _vm?.UpdateBranchFromRemoteCommand.Execute(t);
        };
        Sidebar.GoneDoubleTapped += _ => _vm?.CleanUpBranchesCommand.Execute(null);
        Sidebar.NodeDoubleTapped += node =>
        {
            if (_vm is null) return;
            if (node.IsWorktree && node.Worktree is { } wt) _vm.OpenWorktreeCommand.Execute(wt);
            else if (node.Run is { } run) _vm.OpenUrlCommand.Execute(run.Url);
            else if (node.Target is { Kind: RefKind.LocalBranch or RefKind.RemoteBranch } t) _vm.CheckoutCommand.Execute(t);
        };
        // Anchored to the sidebar, not the row: right-clicking also selects the row, which can refresh the sidebar
        // (opening a pull request fetches its refs) and replace the row, and a menu closes when its row goes.
        Sidebar.NodeContextRequested += (node, _) =>
        {
            if (_vm is null || ShowMenu(Sidebar, _vm.ActionsForSidebar(node)) is not { } menu) return;
            _sidebarMenuOpen = true;
            menu.Closed += (_, _) => _sidebarMenuOpen = false;
        };
        Sidebar.AddWorktreeRequested += () => _vm?.CreateWorktreeCommand.Execute(null);
        Sidebar.RefreshPullRequestsRequested += () => _vm?.RefreshPullRequestListCommand.Execute(null);
        Sidebar.RefreshWorkflowsRequested += () => _vm?.RefreshWorkflowListCommand.Execute(null);
        StagingPane.NodeContextRequested += (node, control) =>
        {
            if (_vm is not null) ShowMenu(control, _vm.ActionsForStagingNode(node));
        };
        DiffView.LineContextRequested += (diff, line, column) =>
        {
            if (_vm is null) return;
            var actions = _vm.ActionsForDiffLine(diff.Path, line, column);
            if (DiffView.HasSelection)
                actions = [new MenuAction("Copy", new RelayCommand(() => _ = DiffView.CopySelectionAsync()), Icon: MenuIcons.Copy), MenuAction.Separator, .. actions];
            ShowMenu(DiffView, actions);
        };
        DetailsView.FileContextRequested += (file, control) =>
        {
            if (_vm is not null) ShowMenu(control, _vm.ActionsForFile(file));
        };
        DetailsView.CopyRequested += sha => _ = _vm?.Dialogs?.CopyToClipboardAsync(sha);
        RecentTiles.ContextRequested += (_, e) =>
        {
            if (_vm is null || (e.Source as Control)?.DataContext is not RecentRepositoryItem item) return;
            ShowMenu(RecentTiles, _vm.ActionsForRecent(item));
            e.Handled = true;
        };
        WorktreeChangesPane.OpenTabRequested += wt => _vm?.OpenWorktreeInTabCommand.Execute(wt);
        WorktreeChangesPane.OpenInCodeRequested += path => _vm?.OpenInVsCodeCommand.Execute(path);
        WorktreeChangesPane.FileContextRequested += (file, folder, control) =>
        {
            if (_vm is not null) ShowMenu(control, _vm.ActionsForFile(file, folder));
        };
    }

    public void FocusFilter()
    {
        if (!_sidebarPinned) ExpandSidebar();
        Sidebar.FocusFilter();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.ScrollToShaRequested -= Graph.ScrollToSha;
            _vm.ReviewWindowRequested -= ShowReviewWindow;
        }
        _vm = DataContext as RepositoryViewModel;
        if (_vm is null) return;

        _vm.ScrollToShaRequested += Graph.ScrollToSha;
        _vm.ReviewWindowRequested += ShowReviewWindow;
        ApplyLayout(_vm.Settings);
    }

    // The review windows of this tab's pull requests: one per pull request, brought to the front when opened again.
    private readonly Dictionary<PullRequestReviewViewModel, PullRequestWindow> _reviewWindows = [];

    private void ShowReviewWindow(PullRequestReviewViewModel review)
    {
        if (_reviewWindows.TryGetValue(review, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var owner = TopLevel.GetTopLevel(this) as Window;
        var window = new PullRequestWindow(review, _vm!.Settings, owner);
        _reviewWindows[review] = window;
        var vm = _vm;
        window.Closed += (_, _) =>
        {
            _reviewWindows.Remove(review);
            vm.OnReviewWindowClosed(review);
        };
        window.Show();
    }

    /// <summary>Pane and column widths are shared by all tabs; apply the saved ones when this tab is shown.</summary>
    public void ApplyLayout(AppSettings s)
    {
        _sidebarWidth = s.SidebarWidth;
        ApplySidebarPin(s.SidebarPinned);
        MainGrid.ColumnDefinitions[4].Width = new GridLength(s.DetailsWidth);
        Graph.Columns = new GraphColumns(s.RefColumnWidth, s.GraphColumnWidth, s.AuthorColumnWidth, s.DateColumnWidth);
    }

    /// <summary>Stores this tab's pane and column widths (when hiding it or closing the window).</summary>
    public void SaveLayout(AppSettings s)
    {
        // Folded, the column is only the rail: keep the width the sidebar opens to.
        if (_sidebarPinned && MainGrid.ColumnDefinitions[0].ActualWidth > 0) s.SidebarWidth = MainGrid.ColumnDefinitions[0].ActualWidth;
        else if (!_sidebarPinned) s.SidebarWidth = _sidebarWidth;
        if (MainGrid.ColumnDefinitions[4].ActualWidth > 0) s.DetailsWidth = MainGrid.ColumnDefinitions[4].ActualWidth;
        SaveGraphColumns(null, s);
    }

    private void SaveGraphColumns(AppSettings? save, AppSettings? into = null)
    {
        var s = into ?? save;
        if (s is null) return;
        var c = Graph.Columns;
        (s.RefColumnWidth, s.GraphColumnWidth, s.AuthorColumnWidth, s.DateColumnWidth) = (c.Ref, c.Graph, c.Author, c.Date);
        save?.Save();
    }

    internal static ContextMenu? ShowMenu(Control target, IReadOnlyList<MenuAction> actions)
    {
        if (actions.Count == 0) return null;
        var menu = new ContextMenu
        {
            ItemsSource = actions.Select(ToMenuItem).ToList(),
            Placement = PlacementMode.Pointer,
        };
        menu.Open(target);
        return menu;
    }

    // ------------------------------------------------------------------ folding sidebar

    private const double RailWidth = 40;
    private bool _sidebarPinned = true;
    private bool _sidebarExpanded;
    private double _sidebarWidth = 240;
    private bool _sidebarMenuOpen;
    private bool _pointerInSidebar;
    private readonly DispatcherTimer _expandTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private void SetUpSidebarFolding()
    {
        Sidebar.RulesRequested += () => _vm?.EditBranchRulesCommand.Execute(null);
        Sidebar.TicketClicked += key => _vm?.OpenTicketCommand.Execute(key);
        Sidebar.PinToggled += pinned =>
        {
            if (pinned == _sidebarPinned || _vm is null) return;
            if (pinned) _sidebarWidth = Math.Max(_sidebarWidth, 160);
            ApplySidebarPin(pinned);
            _vm.Settings.SidebarPinned = pinned;
            _vm.Settings.SidebarWidth = _sidebarWidth;
            _vm.Settings.Save();
        };
        SidebarRail.PointerEntered += (_, _) => _expandTimer.Start();
        SidebarRail.PointerExited += (_, _) => _expandTimer.Stop();
        SidebarRail.PointerPressed += (_, _) => ExpandSidebar();
        _expandTimer.Tick += (_, _) =>
        {
            _expandTimer.Stop();
            ExpandSidebar();
        };
        // Where the pointer is, not enter/leave events: a tooltip popping up under the pointer "leaves" the sidebar
        // without the pointer moving. Tunnel with handled events too, so every move in the tab is seen.
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_sidebarPinned || !_sidebarExpanded) return;
            _pointerInSidebar = IsInSidebar(e.GetPosition(Sidebar));
            if (_pointerInSidebar) _collapseTimer.Stop();
            else if (!_collapseTimer.IsEnabled) _collapseTimer.Start();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Sidebar.PointerExited += (_, e) =>
        {
            // Still inside the sidebar's area: something (a tooltip) opened over it.
            if (IsInSidebar(e.GetPosition(Sidebar))) return;
            _pointerInSidebar = false;
            if (!_sidebarPinned && _sidebarExpanded) _collapseTimer.Start();
        };
        // Keeps checking while something holds the sidebar open (a menu from it, typing in its filter).
        _collapseTimer.Tick += (_, _) =>
        {
            if (_sidebarPinned || !_sidebarExpanded || _pointerInSidebar)
            {
                _collapseTimer.Stop();
                return;
            }
            if (_sidebarMenuOpen || Sidebar.IsFilterFocused) return;
            _collapseTimer.Stop();
            CollapseSidebar();
        };
    }

    /// <summary>Pinned: the sidebar is a resizable column. Unpinned: a rail, with the sidebar opening over the graph.</summary>
    private void ApplySidebarPin(bool pinned)
    {
        _sidebarPinned = pinned;
        Sidebar.IsPinned = pinned;
        _expandTimer.Stop();
        _collapseTimer.Stop();
        _sidebarExpanded = false;
        SidebarRail.IsVisible = !pinned;
        SidebarSplitter.IsVisible = pinned;
        MainGrid.ColumnDefinitions[1].Width = new GridLength(pinned ? 4 : 0);
        MainGrid.ColumnDefinitions[0].Width = new GridLength(pinned ? _sidebarWidth : RailWidth);
        Grid.SetColumnSpan(Sidebar, pinned ? 1 : 3);
        Sidebar.HorizontalAlignment = pinned ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        Sidebar.Width = pinned ? double.NaN : _sidebarWidth;
        Sidebar.Effect = pinned ? null : new DropShadowEffect { BlurRadius = 18, OffsetX = 4, OffsetY = 0, Opacity = 0.55, Color = Colors.Black };
        Sidebar.IsVisible = pinned;
    }

    private void ExpandSidebar()
    {
        if (_sidebarPinned || _sidebarExpanded) return;
        _sidebarExpanded = true;
        Sidebar.IsVisible = true;
        // Opened by pointing at the rail (which it covers), or with Ctrl+Alt+F: then count down until the pointer comes in.
        _pointerInSidebar = SidebarRail.IsPointerOver;
        if (!_pointerInSidebar) _collapseTimer.Start();
    }

    private bool IsInSidebar(Point p) => p.X >= 0 && p.Y >= 0 && p.X < Sidebar.Bounds.Width && p.Y < Sidebar.Bounds.Height;

    private void CollapseSidebar()
    {
        if (_sidebarPinned) return;
        _sidebarExpanded = false;
        Sidebar.IsVisible = false;
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
            Icon = MenuIcon(action),
            // Menus are popups outside the zoomed window content, so they follow the zoom themselves.
            FontSize = 14 * Zoom.Level,
        };

    private static Control? MenuIcon(MenuAction action)
    {
        var z = Zoom.Level;
        switch (action.Icon)
        {
            case Bitmap bitmap:
                return new Image { Source = bitmap, Width = 16 * z, Height = 16 * z };
            case string key when Application.Current?.FindResource(key) is Geometry geometry:
                var icon = new PathIcon { Data = geometry, Width = 14 * z, Height = 14 * z };
                // VS Code keeps its blue, as on the toolbar.
                if (key == MenuIcons.Code) icon.Foreground = new SolidColorBrush(Color.Parse("#3FA0F0"));
                else if (key == MenuIcons.VisualStudio) icon.Foreground = new SolidColorBrush(Color.Parse("#A77BDB"));
                return icon;
            default:
                return null;
        }
    }
}
