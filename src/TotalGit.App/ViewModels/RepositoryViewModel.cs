using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Avatars;
using TotalGit.Core.Git;
using TotalGit.Core.Graph;
using TotalGit.Core.Projects;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

public enum DiffViewMode
{
    Inline,
    Split,
}

/// <summary>Everything the graph control needs to draw one repository.</summary>
public sealed record GraphData(
    GraphLayoutResult Layout,
    IReadOnlyList<RefInfo> Refs,
    (string Owner, string Repo)? GitHubRepo,
    AvatarCache Avatars,
    string RepositoryPath,
    string CurrentWorktreePath,
    IReadOnlyDictionary<string, WorktreeInfo> WorktreesByBranch,
    IReadOnlyDictionary<string, WipInfo> Wip)
{
    /// <summary>Rows that stand for a folded run of commits, by the row's SHA.</summary>
    public IReadOnlyDictionary<string, CommitFold> Folds { get; init; } = new Dictionary<string, CommitFold>();

    /// <summary>Commits folded away, mapped to the folded row that stands for them (where their labels go).</summary>
    public IReadOnlyDictionary<string, string> ShownAs { get; init; } = new Dictionary<string, string>();
}

/// <summary>A WIP row's file count, and the worktree name for rows of other worktrees.</summary>
public sealed record WipInfo(int Count, string? WorktreeName);

/// <summary>One repository tab: its graph, sidebar, details/staging and git actions.</summary>
public partial class RepositoryViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private RepositorySession? _session;
    private RepositoryWatcher? _watcher;
    private RepositoryState? _state;
    private IReadOnlyList<WorktreeInfo> _worktrees = [];
    private WorkingTreeStatus _status = WorkingTreeStatus.Clean;
    private readonly List<CommitInfo> _commits = [];
    private bool _loadingMore;
    private int _detailsRequest;
    private int _diffRequest;

    /// <param name="path">Repository to open when the tab is first shown (restored tabs load lazily).</param>
    public RepositoryViewModel(AvatarCache avatars, AppSettings settings, string? path = null)
    {
        _settings = settings;
        Avatars = avatars;
        Sidebar.Avatars = avatars;
        IconLibrary.Changed += OnBranchRulesChanged;
        settings.TabStyleChanged += OnTabStyleChanged;
        PendingPath = path;
        if (path is not null) RepositoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        DiffMode = Enum.TryParse<DiffViewMode>(settings.DiffMode, out var mode) ? mode : DiffViewMode.Inline;
        WholeFileDiff = settings.DiffWholeFile;
    }

    /// <summary>Path to load when the tab is first selected; null once loaded or for an empty tab.</summary>
    public string? PendingPath { get; private set; }

    /// <summary>The folder this tab shows (for restoring tabs), or null for an empty tab.</summary>
    public string? TabPath => _state?.WorkingDirectory ?? PendingPath;

    public bool IsEmptyTab => TabPath is null && !IsLoading;

    /// <summary>Tab header: repository, plus the worktree when viewing a linked one.</summary>
    public string TabTitle => IsEmptyTab && LoadError is null ? "New tab"
        : WorktreeName is null ? TabDisplayName : $"{TabDisplayName} › {WorktreeName}";

    /// <summary>The repository's name on its tab: the one the user gave it, else the folder's.</summary>
    private string TabDisplayName => TabSettings?.TabName ?? RepositoryName;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Set by the shell so "Open" can use another tab; without it the repository opens in this tab.</summary>
    public Func<Task>? OpenRepositoryHandler { get; set; }

    /// <summary>Raised after a repository has loaded, so the shell can save the open tabs.</summary>
    public event Action? Loaded;

    /// <summary>Starts loading a restored tab the first time it's shown.</summary>
    public void EnsureLoaded()
    {
        if (_state is null && !IsLoading && PendingPath is { } path) _ = LoadAsync(path);
    }

    public void ShowBanner(Banner banner) => Banner = banner;

    public void Dispose()
    {
        CloseReviewWindows();
        _watcher?.Dispose();
        _watcher = null;
        _session?.Dispose();
        _session = null;
        Sidebar.Avatars = null; // the cache is shared by every tab
        IconLibrary.Changed -= OnBranchRulesChanged;
        _settings.TabStyleChanged -= OnTabStyleChanged;
        _wfTimer?.Stop();
        _wfProvider = null;
    }

    public AvatarCache Avatars { get; }
    public SidebarViewModel Sidebar { get; } = new();
    public AppSettings Settings => _settings;

    /// <summary>Set by the view.</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>Raised when the graph should scroll a commit into view.</summary>
    public event Action<string>? ScrollToShaRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepository), nameof(ShowEmptyState))]
    public partial GraphData? Graph { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    public partial string RepositoryName { get; set; } = "No repository";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorktreeName), nameof(TabTitle))]
    public partial string? WorktreeName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentBranchDisplay), nameof(CurrentBranchIcon), nameof(HasCurrentBranchIcon))]
    public partial string CurrentBranch { get; set; } = "-";

    /// <summary>The branch name with a feature/, bug/ or hot-fix/ prefix replaced by <see cref="CurrentBranchIcon"/>.</summary>
    public string CurrentBranchDisplay => BranchCategory.Classify(CurrentBranch).ShortName;
    public Avalonia.Media.Imaging.Bitmap? CurrentBranchIcon => BranchIcons.ForBranch(CurrentBranch);
    public bool HasCurrentBranchIcon => CurrentBranchIcon is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(IsEmptyTab), nameof(TabTitle))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? BusyText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(TabTitle))]
    public partial string? LoadError { get; set; }

    [ObservableProperty]
    public partial Banner? Banner { get; set; }

    [ObservableProperty]
    public partial string? CommitCountText { get; set; }

    [ObservableProperty]
    public partial string? SelectedSha { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetails))]
    public partial CommitDetailsViewModel? Details { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStaging))]
    public partial StagingViewModel? Staging { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiff))]
    public partial FileDiff? Diff { get; set; }

    [ObservableProperty]
    public partial string? DiffTitle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInlineDiff), nameof(IsSplitDiff))]
    public partial DiffViewMode DiffMode { get; set; }

    public bool IsInlineDiff => DiffMode == DiffViewMode.Inline;
    public bool IsSplitDiff => DiffMode == DiffViewMode.Split;

    partial void OnDiffModeChanged(DiffViewMode value)
    {
        _settings.DiffMode = value.ToString();
        _settings.Save();
    }

    [RelayCommand]
    private void SetDiffMode(DiffViewMode mode) => DiffMode = mode;

    /// <summary>Show the whole file with its changes marked, instead of only the changes and the lines round them.</summary>
    [ObservableProperty]
    public partial bool WholeFileDiff { get; set; }

    partial void OnWholeFileDiffChanged(bool value)
    {
        if (_settings.DiffWholeFile != value)
        {
            _settings.DiffWholeFile = value;
            _settings.Save();
        }
        if (Diff is not null) _ = ReloadDiffAsync();
    }

    [ObservableProperty]
    public partial string? AheadText { get; set; }

    [ObservableProperty]
    public partial string? BehindText { get; set; }

    [ObservableProperty]
    public partial string PushToolTip { get; set; } = "Push the current branch";

    public bool HasRepository => Graph is not null;
    public bool HasWorktreeName => WorktreeName is not null;
    public bool ShowEmptyState => Graph is null && !IsLoading && LoadError is null;
    public bool ShowDetails => Details is not null && Range is null;
    public bool ShowStaging => Staging is not null;
    public bool HasDiff => Diff is not null;
    public string? WorkingDirectory => _state?.WorkingDirectory;

    // ------------------------------------------------------------------ loading

    [RelayCommand]
    private async Task OpenRepositoryAsync()
    {
        if (OpenRepositoryHandler is not null)
        {
            await OpenRepositoryHandler();
            return;
        }
        if (Dialogs is null) return;
        var path = await Dialogs.PickFolderAsync();
        if (path is not null) await LoadAsync(path);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_state is not null) await LoadAsync(_state.WorkingDirectory, keepView: true);
    }

    public async Task LoadAsync(string path, bool keepView = false)
    {
        IsLoading = !keepView;
        LoadError = null;
        await _refreshGate.WaitAsync();
        try
        {
            var (session, state, worktrees, status, commits) = await Task.Run(async () =>
            {
                var s = RepositorySession.Open(path);
                try
                {
                    var st = s.LoadState();
                    var wts = await SafeListWorktrees(st.WorkingDirectory);
                    return (s, st, wts, s.GetStatus(), s.ReadHistory());
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            });

            var sameRepo = keepView && _state is not null && WorktreeService.SamePath(_state.WorkingDirectory, state.WorkingDirectory);
            ReplaceSession(session);
            _state = state;
            _worktrees = worktrees;
            _status = status;
            _commits.Clear();
            _commits.AddRange(commits);

            if (!sameRepo)
            {
                ResetGraphFilters();
                SelectedSha = null;
                Details = null;
                Staging = null;
                Diff = null;
                Banner = null;
            }

            _otherWip.Clear();
            ApplyState();
            RebuildGraph();
            UpdatePullRequestHost();
            UpdateWorkflowHost();
            if (sameRepo) await ReloadSelectionAsync();
            _ = RefreshOtherWorktreesAsync();

            PendingPath = null;
            OnPropertyChanged(nameof(TabPath));
            OnPropertyChanged(nameof(IsEmptyTab));
            OnPropertyChanged(nameof(TabTitle));
            if (!sameRepo) _settings.TouchRecent(state.MainWorkingDirectory, state.RepositoryName);
            RefreshTabStyle();
            Loaded?.Invoke();
        }
        catch (Exception ex) when (ex is RepositoryOpenException or LibGit2Sharp.LibGit2SharpException or IOException or GitCommandException)
        {
            if (keepView && Graph is not null)
            {
                ShowError(ex.Message);
            }
            else
            {
                _loadErrorPath = path;
                LoadError = ex.Message;
                Graph = null;
                Sidebar.Clear();
                _prHost = null;
                _prProvider = null;
                CloseReviewWindows();
            }
        }
        finally
        {
            _refreshGate.Release();
            IsLoading = false;
        }
    }

    private static async Task<IReadOnlyList<WorktreeInfo>> SafeListWorktrees(string path)
    {
        try { return await WorktreeService.ListAsync(path); }
        catch (Exception ex) when (ex is GitCommandException or System.ComponentModel.Win32Exception) { return []; }
    }

    private void ReplaceSession(RepositorySession session)
    {
        _watcher?.Dispose();
        _session?.Dispose();
        _session = session;
        _watcher = new RepositoryWatcher(session.WorkingDirectory, session.GitDirectory, session.CommonGitDirectory);
        _watcher.RefsChanged += () => Dispatcher.UIThread.Post(() => _ = RefreshRefsAsync());
        _watcher.WorkingTreeChanged += () => Dispatcher.UIThread.Post(() => _ = RefreshStatusAsync());
    }

    /// <summary>Reloads refs, worktrees, status and the loaded history (after commits, fetches, checkouts…).</summary>
    public async Task RefreshRefsAsync()
    {
        if (_session is not { } session) return;
        await _refreshGate.WaitAsync();
        try
        {
            var target = Math.Max(RepositorySession.PageSize, _commits.Count);
            var (state, worktrees, status, commits) = await Task.Run(async () =>
            {
                var st = session.LoadState();
                var wts = await SafeListWorktrees(st.WorkingDirectory);
                session.ResetHistory();
                var list = new List<CommitInfo>();
                while (list.Count < target && session.HasMoreHistory) list.AddRange(session.ReadHistory(target - list.Count));
                return (st, wts, session.GetStatus(), list);
            });
            if (session != _session) return;

            _state = state;
            _worktrees = worktrees;
            _status = status;
            _commits.Clear();
            _commits.AddRange(commits);
            ApplyState();
            RebuildGraph();
            await ReloadSelectionAsync();
            _ = RefreshOtherWorktreesAsync();
        }
        catch (Exception ex) when (ex is LibGit2Sharp.LibGit2SharpException or IOException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Reloads only the working tree status (WIP row and staging panel).</summary>
    public async Task RefreshStatusAsync()
    {
        if (_session is not { } session) return;
        await _refreshGate.WaitAsync();
        try
        {
            var status = await Task.Run(session.GetStatus);
            if (session != _session) return;
            var wasDirty = _status.IsDirty;
            var countChanged = _status.TotalCount != status.TotalCount;
            _status = status;
            UpdateOperationBanner();
            if (wasDirty != status.IsDirty || countChanged) RebuildGraph();
            Staging?.Update(status);
            if (SelectedSha == CommitInfo.WorkingTreeSha && !status.IsDirty) SelectedSha = null;
            await ReloadDiffAsync();
        }
        catch (Exception ex) when (ex is LibGit2Sharp.LibGit2SharpException or IOException)
        {
            // Transient (e.g. index locked mid-write); the next change event retries.
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task LoadMoreAsync()
    {
        if (_session is not { } session || _loadingMore || !session.HasMoreHistory) return;
        _loadingMore = true;
        CommitCountText = $"{_commits.Count:N0} commits (loading…)";
        try
        {
            await _refreshGate.WaitAsync();
            try
            {
                var page = await Task.Run(() => session.ReadHistory());
                if (session != _session) return;
                _commits.AddRange(page);
                RebuildGraph();
            }
            finally
            {
                _refreshGate.Release();
            }
        }
        finally
        {
            _loadingMore = false;
            UpdateCommitCount();
        }
    }

    private void ApplyState()
    {
        var state = _state!;
        RepositoryName = state.RepositoryName;
        WorktreeName = state.IsLinkedWorktree ? state.WorktreeName : null;
        CurrentBranch = state.CurrentBranch ?? "(detached HEAD)";

        var current = state.Refs.FirstOrDefault(r => r is { Kind: RefKind.LocalBranch, IsCurrent: true });
        AheadText = current is { Ahead: > 0 } ? current.Ahead.ToString() : null;
        BehindText = current is { Behind: > 0 } ? current.Behind.ToString() : null;
        PushToolTip = current is { Ahead: > 0, Behind: > 0 }
            ? "Push the current branch. It has diverged from the remote (after a rebase or amend): right-click the branch to force push."
            : "Push the current branch";

        HasStashes = state.Stashes.Count > 0;
        UpdateOperationBanner();
        LoadJiraProjects(state);
        Sidebar.Update(state.Refs, state.Stashes, _worktrees, WorktreeService.FindLeftovers(state.MainWorkingDirectory, _worktrees), state.WorkingDirectory);
    }

    private void RebuildGraph()
    {
        var state = _state!;
        UpdateAuthors(state);
        var projected = ProjectHistory(state);
        var builder = new GraphLayoutBuilder(TrunkCommits(_commits, state));
        var wip = new Dictionary<string, WipInfo>();
        if (_status.IsDirty && state.HeadSha is not null)
        {
            builder.Append([new CommitInfo(CommitInfo.WorkingTreeSha, [state.HeadSha], "", "", DateTimeOffset.Now,
                "// WIP", IsWorkingTree: true)]);
            wip[CommitInfo.WorkingTreeSha] = new WipInfo(_status.TotalCount, null);
        }
        builder.Append(WithOtherWorktreeRows(projected.Commits, wip));

        var byBranch = _worktrees
            .Where(w => w.Branch is not null)
            .GroupBy(w => w.Branch!)
            .ToDictionary(g => g.Key, g => g.First());

        Graph = new GraphData(
            builder.ToResult(),
            state.Refs,
            AvatarIdentity.ParseGitHubRemote(state.OriginUrl),
            Avatars,
            state.MainWorkingDirectory,
            state.WorkingDirectory,
            byBranch,
            wip) { Folds = projected.Folds, ShownAs = projected.ShownAs };
        UpdateFilterSummary(projected.Commits.Count);
        UpdateCommitCount();
        UpdateBranchOwners(state);
    }

    private void UpdateCommitCount()
    {
        var more = _session?.HasMoreHistory == true;
        CommitCountText = more ? $"{_commits.Count:N0} commits (scroll for more)" : $"{_commits.Count:N0} commits";
    }

    // ------------------------------------------------------------------ selection, details, diff

    partial void OnSelectedShaChanged(string? value) => _ = LoadSelectionAsync(value);

    private Task ReloadSelectionAsync() => SelectedSha == CommitInfo.WorkingTreeSha
        ? Task.CompletedTask // staging already updated with the status
        : SelectedSha is { } sha && Details?.Sha != sha && WorktreeForWip(sha) is null ? LoadSelectionAsync(sha) : Task.CompletedTask;

    private async Task LoadSelectionAsync(string? sha)
    {
        var request = ++_detailsRequest;
        Diff = null;
        SelectedRange = null;

        if (sha is null)
        {
            Details = null;
            Staging = null;
            WorktreeChanges = null;
            return;
        }

        if (WorktreeForWip(sha) is { } otherWorktree)
        {
            Details = null;
            Staging = null;
            WorktreeChanges = null;
            await LoadWorktreeChangesAsync(otherWorktree, request);
            return;
        }
        WorktreeChanges = null;

        if (sha == CommitInfo.WorkingTreeSha)
        {
            Details = null;
            if (Staging is null)
            {
                Staging = CreateStaging();
                Staging.PropertyChanged += OnChildPropertyChanged;
            }
            Staging.Update(_status);
            return;
        }

        Staging = null;
        if (_session is not { } session) return;
        try
        {
            var details = await Task.Run(() => session.GetCommitDetails(sha));
            if (request != _detailsRequest) return;
            var vm = new CommitDetailsViewModel(details, SelectAndReveal, Settings.ChangedFilesTree, SaveChangedFilesTree);
            vm.PropertyChanged += OnChildPropertyChanged;
            Details = vm;
            vm.Avatar = await Avatars.GetAsync(details.Commit.AuthorEmail, Graph?.GitHubRepo, sha);
        }
        catch (Exception ex) when (ex is LibGit2Sharp.LibGit2SharpException or ArgumentException)
        {
            if (request == _detailsRequest) Details = null;
        }
    }

    private void SaveChangedFilesTree(bool tree)
    {
        Settings.ChangedFilesTree = tree;
        Settings.Save();
    }

    private StagingViewModel CreateStaging() => new()
    {
        ShowAsTree = Settings.StagingTree,
        ShowAsTreeChanged = tree =>
        {
            Settings.StagingTree = tree;
            Settings.Save();
        },
        Stage = paths => RunGitAsync("Staging…", () => GitActions.StageAsync(_state!.WorkingDirectory, paths), refresh: Refresh.Status),
        Unstage = paths => RunGitAsync("Unstaging…", () => GitActions.UnstageAsync(_state!.WorkingDirectory, paths), refresh: Refresh.Status),
        StageAll = () => RunGitAsync("Staging…", () => GitActions.StageAllAsync(_state!.WorkingDirectory), refresh: Refresh.Status),
        UnstageAll = () => RunGitAsync("Unstaging…", () => GitActions.UnstageAllAsync(_state!.WorkingDirectory), refresh: Refresh.Status),
        Commit = message => RunGitAsync("Committing…", () => GitActions.CommitAsync(_state!.WorkingDirectory, message)),
    };

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommitDetailsViewModel.SelectedFile)) _ = ReloadDiffAsync();
    }

    private async Task ReloadDiffAsync()
    {
        var request = ++_diffRequest;
        if (_session is not { } session) return;

        FileChangeItem? file;
        Func<FileDiff> load;
        string title;
        var whole = WholeFileDiff;
        if (Staging is { SelectedFile: { Change.Kind: ChangeKind.Conflicted } conflicted } && SelectedSha == CommitInfo.WorkingTreeSha)
        {
            Diff = null;
            // Status refreshes reselect the same file; keep the open tool (and the user's choices).
            if (MergeTool?.Path != conflicted.Path) OpenMergeTool(conflicted.Path);
            return;
        }
        MergeTool = null;

        if (Staging is { SelectedFile: { } sf } && SelectedSha == CommitInfo.WorkingTreeSha)
        {
            file = sf;
            load = () => session.GetWorkingFileDiff(sf.Path, sf.IsStaged, whole);
            title = $"{sf.Path}  ({(sf.IsStaged ? "staged" : "unstaged")})";
        }
        else if (WorktreeChanges is { SelectedFile: { } wf } changes)
        {
            file = wf;
            load = () => changes.Session.GetWorkingFileDiff(wf.Path, wf.IsStaged, whole);
            title = $"{wf.Path}  ({changes.Name}, {(wf.IsStaged ? "staged" : "unstaged")})";
        }
        else if (Details is { SelectedFile: { } df } details)
        {
            file = df;
            load = () => session.GetCommitFileDiff(details.Sha, df.Path, whole);
            title = $"{df.Path}  ({details.ShortSha})";
        }
        else
        {
            Diff = null;
            return;
        }

        try
        {
            var diff = await Task.Run(load);
            if (request != _diffRequest) return;
            DiffTitle = file.Change.OldPath is { } old ? $"{old} → {title}" : title;
            Diff = diff;
        }
        catch (Exception ex) when (ex is LibGit2Sharp.LibGit2SharpException or IOException)
        {
            if (request == _diffRequest) Diff = null;
        }
    }

    [RelayCommand]
    private void CloseDiff()
    {
        MergeTool = null;
        if (Details is not null) Details.SelectedFile = null;
        if (Staging is not null) Staging.SelectedFile = null;
        if (WorktreeChanges is not null) WorktreeChanges.SelectedFile = null;
        Diff = null;
    }

    /// <summary>
    /// Selects a commit and scrolls to it, loading more history if it isn't loaded yet. A commit inside a folded run
    /// unfolds it; one the filters hide selects the row that stands for it.
    /// </summary>
    [RelayCommand]
    private async Task SelectShaAsync(string sha)
    {
        for (var i = 0; i < 25 && Graph is not null && !_commits.Any(c => c.Sha == sha) && _session?.HasMoreHistory == true; i++)
            await LoadMoreAsync();

        if (Graph is { } graph && !graph.Layout.Rows.Any(r => r.Commit.Sha == sha) && _shownAs.TryGetValue(sha, out var row))
        {
            if (graph.Folds.TryGetValue(row, out var fold) && fold.Shas.Contains(sha)) ExpandFold(row);
            else sha = row;
        }

        if (Graph?.Layout.Rows.Any(r => r.Commit.Sha == sha) != true)
        {
            ShowError($"Commit {sha[..Math.Min(7, sha.Length)]} is not in the loaded history.");
            return;
        }
        SelectedSha = sha;
        ScrollToShaRequested?.Invoke(sha);
    }

    private void SelectAndReveal(string sha) => _ = SelectShaAsync(sha);

    public void OnSidebarNodeActivated(SidebarNode node)
    {
        // Stash commits aren't in the graph; just show their changes in the details pane.
        if (node.Stash is { } stash) SelectedSha = stash.Sha;
        else if (node.PullRequest is { } pr) OpenPullRequestCommand.Execute(pr);
        else if (node.Target?.Sha is { } sha) _ = SelectShaAsync(sha);
    }

    // ------------------------------------------------------------------ git actions

    private enum Refresh
    {
        None,
        Status,
        All,
    }

    /// <summary>Runs a git operation with busy state and error banners. Returns true on success.</summary>
    private async Task<bool> RunGitAsync(string busyText, Func<Task> action, string? success = null, Refresh refresh = Refresh.All)
    {
        IsBusy = true;
        BusyText = busyText;
        try
        {
            await action();
            if (success is not null) ShowInfo(success);
            return true;
        }
        catch (BranchInUseException ex)
        {
            var wt = _worktrees.FirstOrDefault(w => WorktreeService.SamePath(w.Path, ex.WorktreePath))
                ?? new WorktreeInfo(ex.WorktreePath, null, null, false, false, false, false, null);
            Banner = new Banner($"That branch is checked out in the worktree at {ex.WorktreePath}.", true, WorktreeOpenActions(wt));
            return false;
        }
        catch (GitCommandException ex) when (ex.Message.Contains("would be overwritten by checkout", StringComparison.Ordinal))
        {
            // Switching branch or commit would lose local edits: offer to stash them, then try again.
            Banner = new Banner(ex.Message, true, [new MenuAction("Stash changes…", StashCommand)]);
            return false;
        }
        catch (GitCommandException ex) when (GitActions.ClassifyPushError(ex.Message) is { } rejection)
        {
            // git's own text is a wall of hints: ask what to do next instead, once this operation has finished.
            Dispatcher.UIThread.Post(() => _ = AskAfterPushRejectedAsync(rejection));
            return false;
        }
        catch (Exception ex) when (ex is GitCommandException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            ShowError(ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            if (refresh == Refresh.All) await RefreshRefsAsync();
            else if (refresh == Refresh.Status) await RefreshStatusAsync();
        }
    }

    [RelayCommand]
    private async Task FetchAsync()
    {
        if (_state is null) return;
        if (await RunGitAsync("Fetching…", () => GitActions.FetchAsync(_state.WorkingDirectory), "Fetched all remotes."))
            OfferCleanUp("Fetched all remotes.");
        _ = RefreshWorkflowsAsync();
    }

    [RelayCommand]
    private async Task PullAsync()
    {
        if (_state is null) return;
        var message = $"Pulled {CurrentBranch}.";
        if (await RunGitAsync("Pulling…", () => GitActions.PullAsync(_state.WorkingDirectory), message))
            OfferCleanUp(message);
    }

    /// <summary>After a fetch or pull: mention local branches whose remote branch is now gone, with a Clean up button.</summary>
    private void OfferCleanUp(string message)
    {
        var gone = GoneBranches().Count;
        if (gone == 0) return;
        var what = gone == 1 ? "1 local branch has lost its remote branch" : $"{gone} local branches have lost their remote branch";
        Banner = new Banner($"{message} {what} (deleted on the remote).", false,
            [new MenuAction("Clean up…", CleanUpBranchesCommand)]);
    }

    [RelayCommand]
    private async Task PushAsync()
    {
        if (_state is null) return;
        // A push usually starts CI: look for the new runs.
        if (await RunGitAsync("Pushing…", () => GitActions.PushAsync(_state.WorkingDirectory), $"Pushed {CurrentBranch}.")) CheckWorkflowsSoon();
    }

    /// <summary>Replaces the remote branch with the local one (after a rebase or amend), after confirming.</summary>
    [RelayCommand]
    private Task ForcePushAsync() => ForcePushCoreAsync(replaceUnseen: false);

    /// <summary>After a refused force push: also replace remote commits that were never on this branch.</summary>
    [RelayCommand]
    private Task ForcePushAnywayAsync() => ForcePushCoreAsync(replaceUnseen: true);

    /// <summary>A push was refused: explain why, list the remote commits involved, and do what the user picks.</summary>
    private async Task AskAfterPushRejectedAsync(PushRejection rejection)
    {
        if (_state?.CurrentBranch is not { } branch || Dialogs is null) return;
        var upstream = _state.Refs.FirstOrDefault(r => r is { Kind: RefKind.LocalBranch, IsCurrent: true })?.Upstream ?? $"the remote {branch}";
        var remoteCommits = await RemoteOnlyCommitsAsync();
        var list = remoteCommits.Count == 0 ? "" : $"\n\nCommits on {upstream} that aren't on your branch:";

        switch (rejection)
        {
            case PushRejection.NonFastForward or PushRejection.FetchFirst:
            {
                var afterRebase = rejection == PushRejection.NonFastForward;
                var message = (afterRebase
                    ? $"{upstream} has commits your branch doesn't, as happens after a rebase or amend. " +
                      "Force push to replace it with your branch, or pull to combine them."
                    : $"Someone else pushed to {upstream}. Pull their changes to combine them with yours, or force push to replace them.") + list;
                var force = new DialogChoice("Force push", IsPrimary: afterRebase, IsDanger: !afterRebase,
                    "Replace the remote branch with yours. Refused if it has commits that were never on your branch.");
                var pull = new DialogChoice("Pull", IsPrimary: !afterRebase, ToolTip: "Bring the remote commits in, then push again.");
                var choice = await Dialogs.ChooseAsync("Push rejected", message, remoteCommits, afterRebase ? [pull, force] : [force, pull]);
                if (choice is null) return;
                var picked = (afterRebase ? new[] { pull, force } : [force, pull])[choice.Value];
                if (picked == force) await ForcePushCoreAsync(replaceUnseen: false, confirmed: true);
                else await PullAsync();
                break;
            }
            case PushRejection.UnseenRemoteCommits:
            {
                var message = $"Force push refused: {upstream} has commits that were never on your branch (someone else pushed, " +
                              "or they were made on the server). Force pushing anyway deletes them from the remote. " +
                              "Check them in the graph, then pull them in or force push anyway." + list;
                var choice = await Dialogs.ChooseAsync("Force push refused", message, remoteCommits,
                    [new DialogChoice("Force push anyway", IsDanger: true), new DialogChoice("Pull", IsPrimary: true)]);
                if (choice == 0) await ForcePushCoreAsync(replaceUnseen: true, confirmed: true);
                else if (choice == 1) await PullAsync();
                break;
            }
            default:
            {
                var choice = await Dialogs.ChooseAsync("Force push refused",
                    $"{upstream} changed since your last fetch, so force pushing could drop commits you haven't seen. " +
                    "Fetch and check what's there first.", null, [new DialogChoice("Fetch", IsPrimary: true)]);
                if (choice == 0) await FetchAsync();
                break;
            }
        }
    }

    /// <summary>"abc1234 subject" for the remote branch's commits that aren't on the local branch (at most 15 lines).</summary>
    private async Task<IReadOnlyList<string>> RemoteOnlyCommitsAsync()
    {
        const int shown = 15;
        var commits = await GitActions.CommitsOnlyOnUpstreamAsync(_state!.WorkingDirectory);
        var lines = commits.Take(shown).ToList();
        if (commits.Count > shown) lines.Add($"… and {commits.Count - shown} more");
        return lines;
    }

    /// <param name="confirmed">The user already chose this in a dialog listing the commits; don't ask again.</param>
    private async Task ForcePushCoreAsync(bool replaceUnseen, bool confirmed = false)
    {
        if (_state?.CurrentBranch is not { } branch || Dialogs is null) return;
        var wt = _state.WorkingDirectory;
        var upstream = _state.Refs.FirstOrDefault(r => r is { Kind: RefKind.LocalBranch, IsCurrent: true })?.Upstream ?? $"the remote {branch}";

        const int shown = 15;
        var replaced = await GitActions.CommitsOnlyOnUpstreamAsync(wt);
        var details = replaced.Take(shown).ToList();
        if (replaced.Count > shown) details.Add($"… and {replaced.Count - shown} more");
        var message = replaceUnseen
            ? $"{upstream} has commits that were never on your branch: someone else pushed them, or they were made on the server. " +
              "Force pushing anyway deletes them from the remote. Check them in the graph first."
            : $"This replaces {upstream} with your local {branch}. Use it after a rebase or amend. " +
              "It's refused if the remote has commits that were never on your branch.";
        if (replaced.Count > 0)
            message += replaceUnseen
                ? $"\n\nThese commits on {upstream} will be removed:"
                : $"\n\nThese commits on {upstream} will be replaced (after a rebase, they're the old copies of yours):";

        if (!confirmed && !await Dialogs.ConfirmAsync($"Force push {branch}?", message, details, replaceUnseen ? "Force push anyway" : "Force push")) return;
        await RunGitAsync("Force pushing…", () => GitActions.PushAsync(wt, force: true, replaceUnseen: replaceUnseen), $"Force pushed {branch}.");
    }

    [RelayCommand]
    private async Task CheckoutAsync(BranchTarget target)
    {
        if (_state is null) return;
        if (target.Kind == RefKind.LocalBranch && target.Worktree is { } wt && !WorktreeService.SamePath(wt.Path, _state.WorkingDirectory))
        {
            Banner = new Banner($"'{target.Name}' is checked out in the worktree at {wt.Path}.", false, WorktreeOpenActions(wt));
            return;
        }

        var name = target.Kind == RefKind.RemoteBranch ? target.ShortName : target.Name;
        if (target.Kind is not (RefKind.RemoteBranch or RefKind.LocalBranch)) return;
        if (await AskLocalChangesAsync("Check out branch", $"Check out {name}. You have uncommitted changes.", "Check out") is not { } mode)
            return;

        var dir = _state.WorkingDirectory;
        if (target.Kind == RefKind.RemoteBranch && target.RemoteName is { } remote)
            await CheckoutWithChangesAsync(name, mode, m => GitActions.CheckoutRemoteAsync(dir, remote, name, m));
        else
            await CheckoutWithChangesAsync(name, mode, m => GitActions.CheckoutAsync(dir, name, m));
    }

    // ------------------------------------------------------------------ worktrees

    [RelayCommand]
    private async Task OpenWorktreeAsync(WorktreeInfo worktree)
    {
        if (!Directory.Exists(worktree.Path))
        {
            ShowError($"{worktree.Path} no longer exists. Use 'Prune stale worktrees' to clean it up.");
            return;
        }
        await LoadAsync(worktree.Path);
    }

    [RelayCommand]
    private void OpenInVsCode(string? path)
    {
        path ??= _state?.WorkingDirectory;
        if (path is null) return;
        try
        {
            VsCodeLauncher.Open(path, _settings.VsCodePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            ShowError(ex.Message);
        }
    }

    /// <summary>
    /// Opens the worktree's solution (.sln/.slnx) with its default program, usually Visual Studio. With several
    /// solutions the user picks one; the choice is remembered per folder and preselected next time.
    /// </summary>
    [RelayCommand]
    private async Task OpenInVisualStudioAsync(string? folder)
    {
        folder ??= _state?.WorkingDirectory;
        if (folder is null) return;
        var solutions = await Task.Run(() => SolutionFinder.Find(folder));
        if (solutions.Count == 0)
        {
            ShowInfo($"No .sln or .slnx file found in {Path.GetFileName(folder.TrimEnd('\\', '/'))}.");
            return;
        }

        var solution = solutions[0];
        if (solutions.Count > 1)
        {
            if (Dialogs is null) return;
            var choices = solutions
                .Select(s => new FormChoice(Path.GetFileName(s), Path.GetRelativePath(folder, Path.GetDirectoryName(s)!) is var dir && dir != "." ? dir : "(top folder)"))
                .ToList();
            var last = _settings.LastSolutions.GetValueOrDefault(folder);
            var pick = FormField.Choice("Solution", choices, Math.Max(0, solutions.ToList().FindIndex(s => s == last)));
            if (!await Dialogs.ShowFormAsync(new FormSpec("Open in Visual Studio", $"{solutions.Count} solutions were found. Which one?", "Open", [pick])))
                return;
            solution = solutions[pick.SelectedIndex];
            _settings.LastSolutions[folder] = solution;
            _settings.Save();
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(solution)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(solution),
            })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowError($"Couldn't open {Path.GetFileName(solution)}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenFileInVsCode(FileTarget target)
    {
        if (_state is null) return;
        try
        {
            var folder = target.Folder ?? _state.WorkingDirectory;
            var full = Path.GetFullPath(Path.Combine(folder, target.Path));
            VsCodeLauncher.OpenFile(folder, full, target.Line, target.Column, _settings.VsCodePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            ShowError(ex.Message);
        }
    }

    [RelayCommand]
    private async Task RevealAsync(string path)
    {
        if (Dialogs is not null) await Dialogs.RevealFolderAsync(path);
    }

    [RelayCommand]
    private async Task CopyAsync(string text)
    {
        if (Dialogs is not null) await Dialogs.CopyToClipboardAsync(text);
    }

    /// <summary>Opens the create-worktree dialog for a branch, tag or commit (null = new branch from HEAD).</summary>
    [RelayCommand]
    private async Task CreateWorktreeAsync(BranchTarget? target)
    {
        if (_state is null || Dialogs is null) return;
        var locals = _state.Refs.Where(r => r.Kind == RefKind.LocalBranch).ToDictionary(r => r.Name);

        WorktreeSource source;
        string branch;
        string? start;
        string startLabel;
        switch (target)
        {
            case { Kind: RefKind.LocalBranch }:
                source = WorktreeSource.LocalBranch;
                branch = target.Name;
                start = null;
                startLabel = target.Name;
                break;
            case { Kind: RefKind.RemoteBranch } when locals.ContainsKey(target.ShortName):
                source = WorktreeSource.LocalBranch;
                branch = target.ShortName;
                start = null;
                startLabel = branch;
                break;
            case { Kind: RefKind.RemoteBranch }:
                source = WorktreeSource.RemoteBranch;
                branch = target.ShortName;
                start = target.Name;
                startLabel = target.Name;
                break;
            case not null:
                source = WorktreeSource.NewBranch;
                branch = "";
                start = target.Sha;
                startLabel = target.Kind == RefKind.Tag ? $"tag {target.Name}" : $"commit {target.Sha[..7]}";
                break;
            default:
                source = WorktreeSource.NewBranch;
                branch = "";
                start = _state.HeadSha ?? "HEAD";
                startLabel = $"HEAD ({CurrentBranch})";
                break;
        }
        await CreateWorktreeFromAsync(source, branch, start, startLabel);
    }

    /// <summary>The create-worktree dialog, then the worktree: for a branch or a start point (a commit, tag or pull request).</summary>
    private async Task CreateWorktreeFromAsync(WorktreeSource source, string branch, string? start, string startLabel)
    {
        if (_state is null || Dialogs is null) return;
        var locals = _state.Refs.Where(r => r.Kind == RefKind.LocalBranch).ToDictionary(r => r.Name);

        // A branch can only be checked out in one worktree.
        if (source == WorktreeSource.LocalBranch)
        {
            var existing = _worktrees.FirstOrDefault(w => w.Branch == branch);
            if (existing is not null)
            {
                Banner = new Banner($"'{branch}' already has a worktree at {existing.Path}.", false, WorktreeOpenActions(existing));
                return;
            }
        }

        var repoSettings = _settings.ForRepository(_state.MainWorkingDirectory);
        var vm = new CreateWorktreeViewModel(
            _state.MainWorkingDirectory, source, branch, start, startLabel, locals.Keys,
            repoSettings.LocalFilePatterns ?? WorktreeProvisioner.DefaultLocalFilePatterns,
            repoSettings.CopyLocalFiles, repoSettings.LinkNodeModules);
        if (!await Dialogs.ShowCreateWorktreeAsync(vm)) return;

        repoSettings.LocalFilePatterns = vm.PatternList.ToList();
        repoSettings.CopyLocalFiles = vm.CopyLocalFiles;
        repoSettings.LinkNodeModules = vm.LinkNodeModules;
        _settings.Save();

        WorktreeCreateResult? result = null;
        var ok = await RunGitAsync($"Creating worktree {vm.Name}…", async () =>
            result = await Task.Run(() => WorktreeProvisioner.CreateAsync(vm.ToRequest())));
        if (!ok || result is null) return;

        var parts = new List<string> { $"Created worktree .worktrees/{vm.Name} on {result.Branch}." };
        if (vm.CopyLocalFiles) parts.Add(result.CopiedFiles.Count == 0 ? "No local files to copy." : $"Copied {string.Join(", ", result.CopiedFiles)}.");
        if (result.SkippedFiles.Count > 0) parts.Add($"Skipped existing {string.Join(", ", result.SkippedFiles)}.");
        if (result.LinkedFolders.Count > 0) parts.Add($"Linked {string.Join(", ", result.LinkedFolders)}.");
        if (result.GitIgnoreUpdated) parts.Add("Added .worktrees/ to .gitignore.");

        var created = _worktrees.FirstOrDefault(w => WorktreeService.SamePath(w.Path, result.Path))
            ?? new WorktreeInfo(result.Path, result.Branch, null, false, false, false, false, null);
        Banner = new Banner(string.Join(" ", parts), false, WorktreeOpenActions(created));
    }

    [RelayCommand]
    private async Task RemoveWorktreeAsync(WorktreeInfo worktree)
    {
        if (_state is null || Dialogs is null || worktree.IsMain) return;

        var branchNote = worktree.Branch is null ? "" : $" The branch '{worktree.Branch}' is kept.";
        if (!await Dialogs.ConfirmAsync("Remove worktree",
                $"Remove the worktree '{worktree.Name}' and delete its folder?{branchNote}",
                [worktree.Path], "Remove"))
            return;

        await RemoveWorktreeFolderAsync(worktree.Path);
    }

    /// <summary>Deletes a leftover folder under .worktrees that git no longer tracks.</summary>
    [RelayCommand]
    private async Task DeleteLeftoverAsync(string path)
    {
        if (_state is null || Dialogs is null) return;
        if (!await Dialogs.ConfirmAsync("Delete leftover folder",
                $"'{Path.GetFileName(path)}' is no longer a registered worktree. Delete the folder and everything in it?",
                [path], "Delete"))
            return;
        await RemoveWorktreeFolderAsync(path);
    }

    /// <summary>Retries a removal that was blocked by a locked file (the user already confirmed it).</summary>
    [RelayCommand]
    private Task RetryRemoveAsync(string path) => RemoveWorktreeFolderAsync(path);

    private async Task RemoveWorktreeFolderAsync(string path)
    {
        if (_state is null || Dialogs is null) return;
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        var mainRoot = _state.MainWorkingDirectory;
        if (WorktreeService.SamePath(path, _state.WorkingDirectory))
            await LoadAsync(mainRoot);

        var force = false;
        while (true)
        {
            try
            {
                IsBusy = true;
                BusyText = $"Removing worktree {name}…";
                await Task.Run(() => WorktreeProvisioner.RemoveAsync(mainRoot, path, force));
                ShowInfo($"Removed worktree {name}.");
                break;
            }
            catch (WorktreeDirtyException ex)
            {
                IsBusy = false;
                if (!await Dialogs.ConfirmAsync("Worktree has changes",
                        $"'{name}' has uncommitted changes that will be lost:", ex.Changes.Take(50).ToArray(), "Force remove"))
                    break;
                force = true;
            }
            catch (WorktreeLockedException ex)
            {
                var relative = Path.GetRelativePath(path, ex.LockedPath);
                Banner = new Banner(
                    $"Couldn't finish removing '{name}': {relative} is in use by another program. " +
                    "Close any VS Code window, terminal or dev server using this worktree, then try again.",
                    true,
                    [new MenuAction("Try again", RetryRemoveCommand, path), new MenuAction("Reveal folder", RevealCommand, path, Icon: MenuIcons.Folder)]);
                break;
            }
            catch (Exception ex) when (ex is GitCommandException or IOException or UnauthorizedAccessException)
            {
                ShowError(ex.Message);
                break;
            }
            finally
            {
                IsBusy = false;
                BusyText = null;
            }
        }
        await RefreshRefsAsync();
    }

    [RelayCommand]
    private Task PruneWorktreesAsync() => _state is null ? Task.CompletedTask
        : RunGitAsync("Pruning worktrees…", () => WorktreeProvisioner.PruneAsync(_state.MainWorkingDirectory), "Pruned stale worktrees.");

    // ------------------------------------------------------------------ menus

    private IReadOnlyList<MenuAction> WorktreeOpenActions(WorktreeInfo wt) =>
    [
        new("Open in Total Git", OpenWorktreeCommand, wt, Icon: MenuIcons.Open),
        new("Open in VS Code", OpenInVsCodeCommand, wt.Path, Icon: MenuIcons.Code),
        new("Open in Visual Studio", OpenInVisualStudioCommand, wt.Path, Icon: MenuIcons.VisualStudio),
    ];

    /// <summary>Context menu for a branch, tag or worktree (shared by sidebar and graph).</summary>
    public IReadOnlyList<MenuAction> ActionsFor(BranchTarget target, bool fromWorktreeSection = false)
    {
        var actions = new List<MenuAction>();
        var current = _state?.WorkingDirectory ?? "";
        var wt = target.Worktree;
        var isCurrentWorktree = wt is not null && WorktreeService.SamePath(wt.Path, current);

        if (wt is not null && (target.Kind == RefKind.LocalBranch || fromWorktreeSection))
        {
            if (!isCurrentWorktree) actions.Add(new MenuAction("Open worktree in Total Git", OpenWorktreeCommand, wt, IsEnabled: !wt.IsPrunable, Icon: MenuIcons.Open));
            actions.Add(new MenuAction(isCurrentWorktree ? "Open in VS Code" : "Open worktree in VS Code", OpenInVsCodeCommand, wt.Path, IsEnabled: !wt.IsPrunable, Icon: MenuIcons.Code));
            actions.Add(new MenuAction(isCurrentWorktree ? "Open in Visual Studio" : "Open worktree in Visual Studio", OpenInVisualStudioCommand, wt.Path, IsEnabled: !wt.IsPrunable, Icon: MenuIcons.VisualStudio));
            actions.Add(new MenuAction("Reveal folder", RevealCommand, wt.Path, IsEnabled: !wt.IsPrunable, Icon: MenuIcons.Folder));
            if (!wt.IsMain)
            {
                actions.Add(MenuAction.Separator);
                actions.Add(new MenuAction("Remove worktree…", RemoveWorktreeCommand, wt, Icon: MenuIcons.Delete));
            }
            if (wt.IsPrunable) actions.Add(new MenuAction("Prune stale worktrees", PruneWorktreesCommand));
            if (fromWorktreeSection)
            {
                actions.Add(MenuAction.Separator);
                actions.Add(new MenuAction("Copy path", CopyCommand, wt.Path, Icon: MenuIcons.Copy));
                return actions;
            }
            actions.Add(MenuAction.Separator);
        }

        var isCheckedOutHere = target.Kind == RefKind.LocalBranch && _state?.CurrentBranch == target.Name;
        var dangerous = new List<MenuAction>();
        switch (target.Kind)
        {
            case RefKind.LocalBranch:
                if (!isCheckedOutHere && wt is null) actions.Add(new MenuAction($"Checkout {target.Name}", CheckoutCommand, target, Icon: MenuIcons.Checkout));
                if (wt is null) actions.Add(new MenuAction("Create worktree…", CreateWorktreeCommand, target, Icon: MenuIcons.Worktree));
                if (isCheckedOutHere && wt is null)
                {
                    actions.Add(new MenuAction("Open in VS Code", OpenInVsCodeCommand, current, Icon: MenuIcons.Code));
                    actions.Add(new MenuAction("Open in Visual Studio", OpenInVisualStudioCommand, current, Icon: MenuIcons.VisualStudio));
                }
                actions.Add(new MenuAction("Create branch here…", CreateBranchCommand, target, Icon: MenuIcons.Branch));
                actions.Add(new MenuAction("Create tag here…", CreateTagCommand, target, Icon: MenuIcons.Tag));
                actions.Add(new MenuAction("Copy branch name", CopyCommand, target.Name, Icon: MenuIcons.Copy));
                if (isCheckedOutHere && _state!.Refs.Any(r => r is { Kind: RefKind.LocalBranch, IsCurrent: true, Upstream: not null, UpstreamGone: false }))
                    dangerous.Add(new MenuAction("Force push…", ForcePushCommand, Icon: MenuIcons.Push));
                if (!isCheckedOutHere && wt is null) dangerous.Add(new MenuAction("Delete branch…", DeleteBranchCommand, target, Icon: MenuIcons.Delete));
                if (_state?.Refs.Any(r => r is { Kind: RefKind.LocalBranch, UpstreamGone: true } && r.Name == target.Name) == true)
                    dangerous.Add(new MenuAction("Clean up branches deleted on remote…", CleanUpBranchesCommand, Icon: MenuIcons.Delete));
                break;
            case RefKind.RemoteBranch:
                actions.Add(new MenuAction($"Checkout {target.ShortName}", CheckoutCommand, target, Icon: MenuIcons.Checkout));
                actions.Add(new MenuAction("Create worktree…", CreateWorktreeCommand, target, Icon: MenuIcons.Worktree));
                actions.Add(new MenuAction("Create branch here…", CreateBranchCommand, target, Icon: MenuIcons.Branch));
                actions.Add(new MenuAction("Create tag here…", CreateTagCommand, target, Icon: MenuIcons.Tag));
                actions.Add(new MenuAction("Copy branch name", CopyCommand, target.Name, Icon: MenuIcons.Copy));
                // The remote's default branch can't be deleted (the host refuses, and everything is based on it).
                var isDefault = _state?.DefaultBranch is { } main && (main == target.ShortName || main == target.Name);
                dangerous.Add(new MenuAction(isDefault ? "Delete from remote (default branch)" : "Delete from remote…",
                    DeleteRemoteBranchCommand, target, IsEnabled: !isDefault, Icon: MenuIcons.Delete));
                break;
            case RefKind.Tag:
                actions.Add(new MenuAction("Create worktree from tag…", CreateWorktreeCommand, target, Icon: MenuIcons.Worktree));
                actions.Add(new MenuAction("Push tag", PushTagCommand, target, Icon: MenuIcons.Push));
                actions.Add(new MenuAction("Copy tag name", CopyCommand, target.Name, Icon: MenuIcons.Copy));
                dangerous.Add(new MenuAction("Delete tag…", DeleteTagCommand, target, Icon: MenuIcons.Delete));
                dangerous.Add(new MenuAction("Delete tag from remote…", DeleteRemoteTagCommand, target, Icon: MenuIcons.Delete));
                break;
        }

        if (target.Kind is RefKind.LocalBranch or RefKind.RemoteBranch && !(target.Kind == RefKind.RemoteBranch && target.ShortName == _state?.DefaultBranch))
        {
            actions.Add(MenuAction.Separator);
            actions.Add(new MenuAction("Review changes against base", ReviewBranchNearestCommand, target, Icon: MenuIcons.Open));
            actions.Add(new MenuAction("Review against…", ReviewBranchAgainstCommand, target));
        }
        if (target.Kind is RefKind.LocalBranch or RefKind.RemoteBranch && TicketInBranch(target.ShortName) is { } ticket)
            actions.Add(new MenuAction($"Open {ticket} in Jira", OpenTicketCommand, ticket, Icon: MenuIcons.Browser));

        var mergeRebase = isCheckedOutHere ? [] : MergeRebaseActions(target, isCurrentTip: _state?.HeadSha == target.Sha).ToList();
        if (mergeRebase.Count > 0)
        {
            actions.Add(MenuAction.Separator);
            actions.AddRange(mergeRebase);
        }
        if (dangerous.Count > 0)
        {
            actions.Add(MenuAction.Separator);
            actions.AddRange(dangerous);
        }
        return actions;
    }

    /// <summary>Context menu for a graph row: one submenu per ref on the commit, then commit actions.</summary>
    public IReadOnlyList<MenuAction> ActionsForCommit(CommitInfo commit)
    {
        if (ActionsForRange(commit) is { } rangeActions) return rangeActions;
        if (commit.IsOtherWorktree) return ActionsForOtherWip(commit);
        if (commit.IsWorkingTree)
            return
            [
                new MenuAction("Open in VS Code", OpenInVsCodeCommand, _state?.WorkingDirectory, Icon: MenuIcons.Code),
                new MenuAction("Open in Visual Studio", OpenInVisualStudioCommand, _state?.WorkingDirectory, Icon: MenuIcons.VisualStudio),
            ];

        var actions = new List<MenuAction>();
        // Including labels of commits folded into this row.
        var refs = Graph?.Refs.Where(r => Graph.ShownAs.GetValueOrDefault(r.TargetSha, r.TargetSha) == commit.Sha
                                          && r.Kind != RefKind.DetachedHead).ToList() ?? [];
        foreach (var r in refs)
        {
            Graph!.WorktreesByBranch.TryGetValue(r.Kind == RefKind.LocalBranch ? r.Name : "", out var wt);
            actions.Add(new MenuAction(r.Name, Children: ActionsFor(BranchTarget.From(r, wt))));
        }
        if (actions.Count > 0) actions.Add(MenuAction.Separator);

        var here = new BranchTarget(RefKind.DetachedHead, commit.ShortSha, commit.Sha);
        var isCheckedOut = _state?.HeadSha == commit.Sha && _state.CurrentBranch is null;
        actions.Add(new MenuAction("Check out this commit…", CheckoutCommitCommand, commit, IsEnabled: !isCheckedOut && !IsOperationInProgress, Icon: MenuIcons.Checkout));
        actions.Add(new MenuAction("Create branch here…", CreateBranchCommand, here, Icon: MenuIcons.Branch));
        actions.Add(new MenuAction("Create tag here…", CreateTagCommand, here, Icon: MenuIcons.Tag));
        actions.Add(new MenuAction("Create worktree from this commit…", CreateWorktreeCommand, here, Icon: MenuIcons.Worktree));
        actions.Add(new MenuAction("Copy commit SHA", CopyCommand, commit.Sha, Icon: MenuIcons.Copy));
        actions.Add(new MenuAction("Copy commit message", CopyCommand, commit.MessageShort, Icon: MenuIcons.Copy));
        if (FoldActions(commit).ToList() is { Count: > 0 } fold)
        {
            actions.Add(MenuAction.Separator);
            actions.AddRange(fold);
        }

        var rewrite = MergeRebaseActions(here, isCurrentTip: _state?.HeadSha == commit.Sha).ToList();
        if (_state?.CurrentBranch is not null && !IsOperationInProgress)
            rewrite.Add(new MenuAction("Interactive rebase from here…", InteractiveRebaseCommand, commit, Icon: MenuIcons.Rebase));
        if (_state is not null && !IsOperationInProgress)
            rewrite.Add(new MenuAction($"Reset {_state.CurrentBranch ?? "HEAD"} to here…", ResetToCommitCommand, commit, Icon: MenuIcons.Reset));
        if (rewrite.Count > 0)
        {
            actions.Add(MenuAction.Separator);
            actions.AddRange(rewrite);
        }
        return actions;
    }

    /// <summary>Context menu for one ref picked from a commit's fanned-out refs in the graph.</summary>
    public IReadOnlyList<MenuAction> ActionsForRef(RefInfo r)
    {
        if (r.Kind == RefKind.DetachedHead || Graph is null) return [];
        Graph.WorktreesByBranch.TryGetValue(r.Kind == RefKind.LocalBranch ? r.Name : "", out var wt);
        return ActionsFor(BranchTarget.From(r, wt));
    }

    /// <summary>Double-click on a ref in the graph's fan: check the branch out, as double-clicking it in the sidebar does.</summary>
    public void ActivateRef(RefInfo r)
    {
        if (r.Kind is not (RefKind.LocalBranch or RefKind.RemoteBranch) || Graph is null) return;
        Graph.WorktreesByBranch.TryGetValue(r.Kind == RefKind.LocalBranch ? r.Name : "", out var wt);
        var target = BranchTarget.From(r, wt);
        if (target.Kind == RefKind.LocalBranch && _state?.CurrentBranch == target.Name) return;
        CheckoutCommand.Execute(target);
    }

    // ------------------------------------------------------------------ branch rules

    private void OnBranchRulesChanged()
    {
        Sidebar.RefreshRules();
        // Which lines are main lines (drawn thicker) comes from the rules too.
        if (_state is not null && Graph is not null) RebuildGraph();
        OnPropertyChanged(nameof(CurrentBranchDisplay));
        OnPropertyChanged(nameof(CurrentBranchIcon));
        OnPropertyChanged(nameof(HasCurrentBranchIcon));
    }

    /// <summary>Opens the branch rules editor; with a pattern, starts a new rule for it (from a branch's or folder's menu).</summary>
    [RelayCommand]
    private async Task EditBranchRulesAsync(string? newPattern)
    {
        if (Dialogs is null) return;
        var names = _state?.Refs
            .Where(r => r.Kind is RefKind.LocalBranch or RefKind.RemoteBranch)
            .Select(r => r.Kind == RefKind.RemoteBranch ? r.ShortName : r.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        var vm = new BranchRulesViewModel(BranchRuleSet.Current.Rules, names,
            (title, message) => Dialogs.ConfirmAsync(title, message, null, "Reset"), newPattern);
        if (!await Dialogs.ShowBranchRulesAsync(vm)) return;
        Settings.BranchRules = vm.Result();
        Settings.ApplyBranchRules();
        Settings.Save();
        IconLibrary.NotifyChanged();
    }

    /// <summary>The pattern a new rule for this branch starts with: its prefix ("bug/x" → "bug/*"), or its name.</summary>
    private static string RulePatternFor(string name) => name.LastIndexOf('/') is var i and > 0 ? name[..(i + 1)] + "*" : name;

    public IReadOnlyList<MenuAction> ActionsForSidebar(SidebarNode node)
    {
        var actions = ActionsForSidebarNode(node);
        // Any branch or folder can be given its own icon or group.
        string? pattern = node.IsFolder && node.BranchPrefix is { } prefix ? prefix + "*"
            : node.Target is { Kind: RefKind.LocalBranch } local && !node.IsWorktree ? RulePatternFor(local.Name)
            : node.Kind == SidebarNodeKind.RemoteBranch && node.Target is { } remote ? RulePatternFor(ShortRemote(remote.Name))
            : null;
        if (pattern is null) return actions;
        return [.. actions, .. actions.Count > 0 ? new[] { MenuAction.Separator } : [], new MenuAction("Branch icon and grouping…", EditBranchRulesCommand, pattern)];
    }

    private string ShortRemote(string name) =>
        _state?.Refs.FirstOrDefault(r => r.Kind == RefKind.RemoteBranch && r.Name == name)?.ShortName ?? name;

    private IReadOnlyList<MenuAction> ActionsForSidebarNode(SidebarNode node)
    {
        if (node.IsWorktreesSection)
        {
            return
            [
                new MenuAction("Create worktree with new branch…", CreateWorktreeCommand, Icon: MenuIcons.Worktree),
                new MenuAction("Prune stale worktrees", PruneWorktreesCommand),
            ];
        }
        if (node.IsPullRequestsSection)
            return [new MenuAction("Refresh pull requests", RefreshPullRequestListCommand, Icon: MenuIcons.Refresh)];
        if (node.IsWorkflowsSection)
            return
            [
                new MenuAction("Refresh workflows", RefreshWorkflowListCommand, Icon: MenuIcons.Refresh),
                MenuAction.Separator,
                new MenuAction("Hide workflows", HideWorkflowsCommand),
            ];
        if (node.Run is { } run)
            return ActionsForRun(run);
        if (node.PullRequest is { } pullRequest)
            return ActionsForPullRequest(pullRequest);
        if (node.IsFolder && node.BranchPrefix is { } prefix)
            return [new MenuAction($"Create branch in {prefix}…", CreateBranchInFolderCommand, prefix, Icon: (object?)node.KindIcon ?? MenuIcons.Branch)];
        if (node.IsSection && node.Label == "LOCAL")
        {
            var gone = GoneBranches().Count;
            return
            [
                new MenuAction("Create branch…", CreateBranchAtHeadCommand, Icon: MenuIcons.Branch),
                MenuAction.Separator,
                new MenuAction(gone > 0 ? $"Clean up branches deleted on remote ({gone})…" : "Clean up branches deleted on remote…",
                    CleanUpBranchesCommand, Icon: MenuIcons.Delete),
            ];
        }
        if (node.Stash is { } stash)
        {
            return
            [
                new MenuAction("Apply stash", ApplyStashCommand, stash, Icon: MenuIcons.Pop),
                new MenuAction("Pop stash", PopStashCommand, stash, Icon: MenuIcons.Pop),
                MenuAction.Separator,
                new MenuAction("Delete stash…", DropStashCommand, stash, Icon: MenuIcons.Delete),
            ];
        }
        if (node.LeftoverPath is { } leftover)
        {
            return
            [
                new MenuAction("Delete leftover folder…", DeleteLeftoverCommand, leftover, Icon: MenuIcons.Delete),
                new MenuAction("Reveal folder", RevealCommand, leftover, Icon: MenuIcons.Folder),
                MenuAction.Separator,
                new MenuAction("Copy path", CopyCommand, leftover, Icon: MenuIcons.Copy),
            ];
        }
        if (node.IsWorktree && node.Worktree is { } wt)
            return ActionsFor(node.Target ?? new BranchTarget(RefKind.DetachedHead, wt.Name, wt.HeadSha ?? "", Worktree: wt), fromWorktreeSection: true);
        return node.Target is { } t ? ActionsFor(t) : [];
    }

    // ------------------------------------------------------------------ banners

    [RelayCommand]
    private void DismissBanner() => Banner = null;

    private void ShowError(string message) => Banner = new Banner(message, true, []);

    private void ShowInfo(string message) => Banner = new Banner(message, false, []);
}
