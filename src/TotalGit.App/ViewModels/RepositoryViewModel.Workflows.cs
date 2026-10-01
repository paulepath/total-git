using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Hosting;

namespace TotalGit.App.ViewModels;

/// <summary>The WORKFLOWS section: the repository's CI runs (GitHub Actions), refreshed while any are going.</summary>
public partial class RepositoryViewModel
{
    private const int RecentFinishedRuns = 5;

    private IWorkflowProvider? _wfProvider;
    private RemoteHostInfo? _wfHost;
    private IReadOnlyList<WorkflowRun> _runs = [];
    private int _wfRequest;
    private DispatcherTimer? _wfTimer;
    private string? _viewerLogin;

    /// <summary>Set by the main window: whether the app has the focus (runs are checked less often when it hasn't).</summary>
    public static bool AppIsActive { get; set; } = true;

    public IWorkflowProviderFactory? WorkflowProviders { get; init; }

    /// <summary>After a repository loads: picks the workflow provider for its remote and lists its runs.</summary>
    private void UpdateWorkflowHost()
    {
        var host = WorkflowProviders is null || !_settings.ShowWorkflows ? null : RemoteHostParser.Parse(_state?.OriginUrl);
        if (host == _wfHost && _wfProvider is not null) return;
        _wfHost = host;
        _wfProvider = host is null ? null : WorkflowProviders!.TryCreateWorkflows(host);
        _runs = [];
        if (_wfProvider is null)
        {
            _wfTimer?.Stop();
            Sidebar.SetWorkflows(false, [], null, null);
            return;
        }
        _ = RefreshWorkflowsAsync();
    }

    /// <summary>Lists the runs again; keeps checking while any are queued or running.</summary>
    public async Task RefreshWorkflowsAsync(bool fresh = false)
    {
        if (_wfProvider is not { } provider) return;
        var request = ++_wfRequest;
        if (_runs.Count == 0) Sidebar.SetWorkflows(true, [], "Loading…", null);
        try
        {
            _viewerLogin ??= _prProvider is { } pr ? await Task.Run(() => pr.GetViewerAsync()) : null;
            var runs = await Task.Run(() => provider.ListRunsAsync(RecentFinishedRuns, fresh));
            if (request != _wfRequest || provider != _wfProvider) return;
            AnnounceFinishedRuns(_runs, runs);
            _runs = runs;
            Sidebar.SetWorkflows(true, runs, runs.Count == 0 ? "No workflow runs yet" : null, LoadJobsAsync);
        }
        catch (HostException ex)
        {
            if (request != _wfRequest || provider != _wfProvider) return;
            Sidebar.SetWorkflows(true, _runs, ex.Message, LoadJobsAsync);
        }
        ScheduleWorkflowRefresh();
    }

    private Task<IReadOnlyList<WorkflowJob>> LoadJobsAsync(long runId) =>
        _wfProvider is { } p ? Task.Run(() => p.GetJobsAsync(runId)) : Task.FromResult<IReadOnlyList<WorkflowJob>>([]);

    /// <summary>
    /// Every 15 seconds while runs are going and the app has the focus, every minute in the background, and not at
    /// all when nothing is running (coming back to the app, fetching or pushing checks again).
    /// </summary>
    private void ScheduleWorkflowRefresh(TimeSpan? soon = null)
    {
        _wfTimer ??= CreateWorkflowTimer();
        _wfTimer.Stop();
        if (_wfProvider is null) return;
        if (soon is null && !_runs.Any(r => r.IsActive)) return;
        _wfTimer.Interval = soon ?? (AppIsActive ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(1));
        _wfTimer.Start();
    }

    private DispatcherTimer CreateWorkflowTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = RefreshWorkflowsAsync();
        };
        return timer;
    }

    /// <summary>After a push or fetch: runs may have started, so look now and again shortly.</summary>
    private async void CheckWorkflowsSoon()
    {
        if (_wfProvider is null) return;
        await Task.Delay(TimeSpan.FromSeconds(5));
        await RefreshWorkflowsAsync();
        if (!_runs.Any(r => r.IsActive)) ScheduleWorkflowRefresh(TimeSpan.FromSeconds(15));
    }

    /// <summary>A banner when a run the signed-in user started has finished: failures stay until dismissed.</summary>
    private void AnnounceFinishedRuns(IReadOnlyList<WorkflowRun> before, IReadOnlyList<WorkflowRun> now)
    {
        if (_viewerLogin is null) return;
        var wasActive = before.Where(r => r.IsActive).Select(r => r.Id).ToHashSet();
        foreach (var run in now.Where(r => !r.IsActive && wasActive.Contains(r.Id)
                     && string.Equals(r.Actor.Login, _viewerLogin, StringComparison.OrdinalIgnoreCase)))
        {
            var rail = WorkflowTriage.Rail(run);
            if (rail == RunRail.Cancelled) continue;
            var open = new MenuAction("Open", OpenUrlCommand, run.Url);
            Banner = rail == RunRail.Succeeded
                ? new Banner($"{run.WorkflowName} on {run.Branch} passed.", false, [])
                : new Banner($"{run.WorkflowName} on {run.Branch} failed.", true, [open]);
        }
    }

    [RelayCommand]
    private Task RefreshWorkflowList() => RefreshWorkflowsAsync();

    [RelayCommand]
    private async Task CancelRunAsync(WorkflowRun run)
    {
        if (_wfProvider is not { } provider || Dialogs is null) return;
        if (!await Dialogs.ConfirmAsync($"Cancel {run.WorkflowName}?",
                $"Stops run #{run.Number} on {run.Branch}. Jobs that are running are cancelled and the run ends as cancelled.", null, "Cancel run"))
            return;
        await RunWorkflowActionAsync(() => provider.CancelAsync(run.Id), $"Cancelling {run.WorkflowName} #{run.Number}.");
    }

    [RelayCommand]
    private async Task RerunAsync(WorkflowRun run)
    {
        if (_wfProvider is not { } provider || Dialogs is null) return;
        if (!await Dialogs.ConfirmAsync($"Re-run {run.WorkflowName}?",
                $"Runs every job of #{run.Number} on {run.Branch} again, at the same commit.", null, "Re-run"))
            return;
        await RunWorkflowActionAsync(() => provider.RerunAsync(run.Id), $"Re-running {run.WorkflowName} #{run.Number}.");
    }

    [RelayCommand]
    private async Task RerunFailedAsync(WorkflowRun run)
    {
        if (_wfProvider is not { } provider || Dialogs is null) return;
        if (!await Dialogs.ConfirmAsync($"Re-run failed jobs of {run.WorkflowName}?",
                $"Runs the jobs of #{run.Number} on {run.Branch} that failed (and the jobs that depend on them) again.", null, "Re-run failed jobs"))
            return;
        await RunWorkflowActionAsync(() => provider.RerunFailedAsync(run.Id), $"Re-running the failed jobs of {run.WorkflowName} #{run.Number}.");
    }

    private async Task RunWorkflowActionAsync(Func<Task> action, string done)
    {
        try
        {
            await Task.Run(action);
            Banner = new Banner(done, false, []);
        }
        catch (HostException ex)
        {
            ShowError(ex.Message);
        }
        // GitHub takes a moment to show the change, and may answer "unchanged" for a while: ask afresh.
        await Task.Delay(TimeSpan.FromSeconds(3));
        await RefreshWorkflowsAsync(fresh: true);
    }

    [RelayCommand]
    private void HideWorkflows()
    {
        _settings.ShowWorkflows = false;
        _settings.Save();
        UpdateWorkflowHost();
    }

    private IReadOnlyList<MenuAction> ActionsForRun(WorkflowRun run)
    {
        var can = _wfProvider?.Capabilities ?? WorkflowCapabilities.None;
        var actions = new List<MenuAction> { new("Open on GitHub", OpenUrlCommand, run.Url, Icon: MenuIcons.Browser) };
        if (run.IsActive && can.HasFlag(WorkflowCapabilities.Cancel))
            actions.Add(new MenuAction("Cancel run…", CancelRunCommand, run, Icon: MenuIcons.Delete));
        if (!run.IsActive && can.HasFlag(WorkflowCapabilities.Rerun))
            actions.Add(new MenuAction("Re-run…", RerunCommand, run, Icon: MenuIcons.Refresh));
        if (!run.IsActive && WorkflowTriage.Rail(run) == RunRail.Failed && can.HasFlag(WorkflowCapabilities.RerunFailed))
            actions.Add(new MenuAction("Re-run failed jobs…", RerunFailedCommand, run, Icon: MenuIcons.Refresh));
        actions.Add(MenuAction.Separator);
        actions.Add(new MenuAction("Copy link", CopyCommand, run.Url, Icon: MenuIcons.Copy));
        return actions;
    }
}
