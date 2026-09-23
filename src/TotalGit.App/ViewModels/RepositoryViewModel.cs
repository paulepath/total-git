using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Avatars;
using TotalGit.Core.Git;
using TotalGit.Core.Graph;
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
    int WipCount);

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
        PendingPath = path;
        if (path is not null) RepositoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        DiffMode = Enum.TryParse<DiffViewMode>(settings.DiffMode, out var mode) ? mode : DiffViewMode.Inline;
    }

    /// <summary>Path to load when the tab is first selected; null once loaded or for an empty tab.</summary>
    public string? PendingPath { get; private set; }

    /// <summary>The folder this tab shows (for restoring tabs), or null for an empty tab.</summary>
    public string? TabPath => _state?.WorkingDirectory ?? PendingPath;

    public bool IsEmptyTab => TabPath is null && !IsLoading;

    /// <summary>Tab header: repository, plus the worktree when viewing a linked one.</summary>
    public string TabTitle => IsEmptyTab && LoadError is null ? "New tab"
        : WorktreeName is null ? RepositoryName : $"{RepositoryName} › {WorktreeName}";

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
        _watcher?.Dispose();
        _watcher = null;
        _session?.Dispose();
        _session = null;
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

    [ObservableProperty]
    public partial string? AheadText { get; set; }

    [ObservableProperty]
    public partial string? BehindText { get; set; }

    public bool HasRepository => Graph is not null;
    public bool HasWorktreeName => WorktreeName is not null;
    public bool ShowEmptyState => Graph is null && !IsLoading && LoadError is null;
    public bool ShowDetails => Details is not null;
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
                SelectedSha = null;
                Details = null;
                Staging = null;
                Diff = null;
                Banner = null;
            }

            ApplyState();
            RebuildGraph();
            if (sameRepo) await ReloadSelectionAsync();

            PendingPath = null;
            OnPropertyChanged(nameof(TabPath));
            OnPropertyChanged(nameof(IsEmptyTab));
            OnPropertyChanged(nameof(TabTitle));
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
                LoadError = ex.Message;
                Graph = null;
                Sidebar.Clear();
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

        Sidebar.Update(state.Refs, _worktrees, WorktreeService.FindLeftovers(state.MainWorkingDirectory, _worktrees), state.WorkingDirectory);
    }

    private void RebuildGraph()
    {
        var state = _state!;
        var builder = new GraphLayoutBuilder();
        var wipCount = _status.TotalCount;
        if (_status.IsDirty && state.HeadSha is not null)
        {
            builder.Append([new CommitInfo(CommitInfo.WorkingTreeSha, [state.HeadSha], "", "", DateTimeOffset.Now,
                "// WIP", IsWorkingTree: true)]);
        }
        builder.Append(_commits);

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
            wipCount);
        UpdateCommitCount();
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
        : SelectedSha is { } sha && Details?.Sha != sha ? LoadSelectionAsync(sha) : Task.CompletedTask;

    private async Task LoadSelectionAsync(string? sha)
    {
        var request = ++_detailsRequest;
        Diff = null;

        if (sha is null)
        {
            Details = null;
            Staging = null;
            return;
        }

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
            var vm = new CommitDetailsViewModel(details, SelectAndReveal);
            vm.PropertyChanged += OnChildPropertyChanged;
            Details = vm;
            vm.Avatar = await Avatars.GetAsync(details.Commit.AuthorEmail, Graph?.GitHubRepo, sha);
        }
        catch (Exception ex) when (ex is LibGit2Sharp.LibGit2SharpException or ArgumentException)
        {
            if (request == _detailsRequest) Details = null;
        }
    }

    private StagingViewModel CreateStaging() => new()
    {
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
        if (Staging is { SelectedFile: { } sf } && SelectedSha == CommitInfo.WorkingTreeSha)
        {
            file = sf;
            load = () => session.GetWorkingFileDiff(sf.Path, sf.IsStaged);
            title = $"{sf.Path}  ({(sf.IsStaged ? "staged" : "unstaged")})";
        }
        else if (Details is { SelectedFile: { } df } details)
        {
            file = df;
            load = () => session.GetCommitFileDiff(details.Sha, df.Path);
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
        if (Details is not null) Details.SelectedFile = null;
        if (Staging is not null) Staging.SelectedFile = null;
        Diff = null;
    }

    /// <summary>Selects a commit and scrolls to it, loading more history if it isn't loaded yet.</summary>
    [RelayCommand]
    private async Task SelectShaAsync(string sha)
    {
        for (var i = 0; i < 25 && Graph is not null && !Graph.Layout.Rows.Any(r => r.Commit.Sha == sha) && _session?.HasMoreHistory == true; i++)
            await LoadMoreAsync();

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
        if (node.Target?.Sha is { } sha) _ = SelectShaAsync(sha);
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
    private Task FetchAsync() => _state is null ? Task.CompletedTask
        : RunGitAsync("Fetching…", () => GitActions.FetchAsync(_state.WorkingDirectory), "Fetched all remotes.");

    [RelayCommand]
    private Task PullAsync() => _state is null ? Task.CompletedTask
        : RunGitAsync("Pulling…", () => GitActions.PullAsync(_state.WorkingDirectory), $"Pulled {CurrentBranch}.");

    [RelayCommand]
    private Task PushAsync() => _state is null ? Task.CompletedTask
        : RunGitAsync("Pushing…", () => GitActions.PushAsync(_state.WorkingDirectory), $"Pushed {CurrentBranch}.");

    [RelayCommand]
    private async Task CheckoutAsync(BranchTarget target)
    {
        if (_state is null) return;
        if (target.Kind == RefKind.LocalBranch && target.Worktree is { } wt && !WorktreeService.SamePath(wt.Path, _state.WorkingDirectory))
        {
            Banner = new Banner($"'{target.Name}' is checked out in the worktree at {wt.Path}.", false, WorktreeOpenActions(wt));
            return;
        }

        if (target.Kind == RefKind.RemoteBranch && target.RemoteName is { } remote)
            await RunGitAsync($"Checking out {target.ShortName}…", () => GitActions.CheckoutRemoteAsync(_state.WorkingDirectory, remote, target.ShortName));
        else if (target.Kind == RefKind.LocalBranch)
            await RunGitAsync($"Checking out {target.Name}…", () => GitActions.CheckoutAsync(_state.WorkingDirectory, target.Name));
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
                    [new MenuAction("Try again", RetryRemoveCommand, path), new MenuAction("Reveal folder", RevealCommand, path)]);
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
        new("Open in TotalGit", OpenWorktreeCommand, wt),
        new("Open in VS Code", OpenInVsCodeCommand, wt.Path),
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
            if (!isCurrentWorktree) actions.Add(new MenuAction("Open worktree in TotalGit", OpenWorktreeCommand, wt, IsEnabled: !wt.IsPrunable));
            actions.Add(new MenuAction(isCurrentWorktree ? "Open in VS Code" : "Open worktree in VS Code", OpenInVsCodeCommand, wt.Path, IsEnabled: !wt.IsPrunable));
            actions.Add(new MenuAction("Reveal folder", RevealCommand, wt.Path, IsEnabled: !wt.IsPrunable));
            if (!wt.IsMain)
            {
                actions.Add(MenuAction.Separator);
                actions.Add(new MenuAction("Remove worktree…", RemoveWorktreeCommand, wt));
            }
            if (wt.IsPrunable) actions.Add(new MenuAction("Prune stale worktrees", PruneWorktreesCommand));
            if (fromWorktreeSection)
            {
                actions.Add(MenuAction.Separator);
                actions.Add(new MenuAction("Copy path", CopyCommand, wt.Path));
                return actions;
            }
            actions.Add(MenuAction.Separator);
        }

        switch (target.Kind)
        {
            case RefKind.LocalBranch:
                var isCheckedOutHere = _state?.CurrentBranch == target.Name;
                if (!isCheckedOutHere && wt is null) actions.Add(new MenuAction($"Checkout {target.Name}", CheckoutCommand, target));
                if (wt is null) actions.Add(new MenuAction("Create worktree…", CreateWorktreeCommand, target));
                if (isCheckedOutHere && wt is null) actions.Add(new MenuAction("Open in VS Code", OpenInVsCodeCommand, current));
                actions.Add(new MenuAction("Copy branch name", CopyCommand, target.Name));
                break;
            case RefKind.RemoteBranch:
                actions.Add(new MenuAction($"Checkout {target.ShortName}", CheckoutCommand, target));
                actions.Add(new MenuAction("Create worktree…", CreateWorktreeCommand, target));
                actions.Add(new MenuAction("Copy branch name", CopyCommand, target.Name));
                break;
            case RefKind.Tag:
                actions.Add(new MenuAction("Create worktree from tag…", CreateWorktreeCommand, target));
                actions.Add(new MenuAction("Copy tag name", CopyCommand, target.Name));
                break;
        }
        return actions;
    }

    /// <summary>Context menu for a graph row: one submenu per ref on the commit, then commit actions.</summary>
    public IReadOnlyList<MenuAction> ActionsForCommit(CommitInfo commit)
    {
        if (commit.IsWorkingTree)
            return [new MenuAction("Open in VS Code", OpenInVsCodeCommand, _state?.WorkingDirectory)];

        var actions = new List<MenuAction>();
        var refs = Graph?.Refs.Where(r => r.TargetSha == commit.Sha && r.Kind != RefKind.DetachedHead).ToList() ?? [];
        foreach (var r in refs)
        {
            Graph!.WorktreesByBranch.TryGetValue(r.Kind == RefKind.LocalBranch ? r.Name : "", out var wt);
            actions.Add(new MenuAction(r.Name, Children: ActionsFor(BranchTarget.From(r, wt))));
        }
        if (actions.Count > 0) actions.Add(MenuAction.Separator);

        actions.Add(new MenuAction("Create worktree from this commit…", CreateWorktreeCommand,
            new BranchTarget(RefKind.DetachedHead, commit.ShortSha, commit.Sha)));
        actions.Add(new MenuAction("Copy commit SHA", CopyCommand, commit.Sha));
        actions.Add(new MenuAction("Copy commit message", CopyCommand, commit.MessageShort));
        return actions;
    }

    public IReadOnlyList<MenuAction> ActionsForSidebar(SidebarNode node)
    {
        if (node.IsWorktreesSection)
        {
            return
            [
                new MenuAction("Create worktree with new branch…", CreateWorktreeCommand),
                new MenuAction("Prune stale worktrees", PruneWorktreesCommand),
            ];
        }
        if (node.LeftoverPath is { } leftover)
        {
            return
            [
                new MenuAction("Delete leftover folder…", DeleteLeftoverCommand, leftover),
                new MenuAction("Reveal folder", RevealCommand, leftover),
                MenuAction.Separator,
                new MenuAction("Copy path", CopyCommand, leftover),
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
