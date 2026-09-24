using Avalonia;
using Avalonia.Controls;
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
        Graph.AttachScrollBar(GraphScrollBar);
        DiffView.AttachScrollBar(DiffScrollBar);

        Graph.NearEnd += () => _ = _vm?.LoadMoreAsync();
        Graph.ColumnsChanged += () => SaveGraphColumns(_vm?.Settings);
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
        StagingPane.NodeContextRequested += (node, control) =>
        {
            if (_vm is not null) ShowMenu(control, _vm.ActionsForStagingNode(node));
        };
        DiffView.LineContextRequested += (diff, line, column) =>
        {
            if (_vm is null) return;
            var actions = _vm.ActionsForDiffLine(diff.Path, line, column);
            if (DiffView.HasSelection)
                actions = [new MenuAction("Copy", new RelayCommand(() => _ = DiffView.CopySelectionAsync())), MenuAction.Separator, .. actions];
            ShowMenu(DiffView, actions);
        };
        DetailsView.FileContextRequested += (file, control) =>
        {
            if (_vm is not null) ShowMenu(control, _vm.ActionsForFile(file));
        };
        DetailsView.CopyRequested += sha => _ = _vm?.Dialogs?.CopyToClipboardAsync(sha);
    }

    public void FocusFilter() => Sidebar.FocusFilter();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.ScrollToShaRequested -= Graph.ScrollToSha;
        _vm = DataContext as RepositoryViewModel;
        if (_vm is null) return;

        _vm.ScrollToShaRequested += Graph.ScrollToSha;
        ApplyLayout(_vm.Settings);
    }

    /// <summary>Pane and column widths are shared by all tabs; apply the saved ones when this tab is shown.</summary>
    public void ApplyLayout(AppSettings s)
    {
        MainGrid.ColumnDefinitions[0].Width = new GridLength(s.SidebarWidth);
        MainGrid.ColumnDefinitions[4].Width = new GridLength(s.DetailsWidth);
        Graph.Columns = new GraphColumns(s.RefColumnWidth, s.GraphColumnWidth, s.AuthorColumnWidth, s.DateColumnWidth);
    }

    /// <summary>Stores this tab's pane and column widths (when hiding it or closing the window).</summary>
    public void SaveLayout(AppSettings s)
    {
        if (MainGrid.ColumnDefinitions[0].ActualWidth > 0) s.SidebarWidth = MainGrid.ColumnDefinitions[0].ActualWidth;
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
}
