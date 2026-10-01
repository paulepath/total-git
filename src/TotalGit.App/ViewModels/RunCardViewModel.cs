using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TotalGit.App.Services;
using TotalGit.Core.Hosting;

namespace TotalGit.App.ViewModels;

/// <summary>A job's line on a run's hover card.</summary>
public sealed record RunJobLine(string Name, IBrush Brush, string StateText, string? Step)
{
    public bool HasStep => Step is not null;
}

/// <summary>
/// The hover card of a workflow run row: what started it, on which branch and commit, how long it has taken, and
/// its jobs (loaded the first time the card opens).
/// </summary>
public sealed partial class RunCardViewModel : ObservableObject
{
    private readonly Func<long, Task<IReadOnlyList<WorkflowJob>>>? _loadJobs;
    private bool _jobsRequested;

    public RunCardViewModel(WorkflowRun run, PrPerson actor, Func<long, Task<IReadOnlyList<WorkflowJob>>>? loadJobs)
    {
        Run = run;
        Actor = actor;
        _loadJobs = loadJobs;
        var rail = WorkflowTriage.Rail(run);
        RailBrush = RunRailColors.For(rail);
        StatusText = rail switch
        {
            RunRail.Queued => run.Status == RunStatus.Waiting ? "Waiting (approval or a free runner)" : "Queued",
            RunRail.Running => "Running",
            RunRail.Succeeded => "Passed",
            RunRail.Cancelled => "Cancelled",
            _ => run.Conclusion switch
            {
                RunConclusion.TimedOut => "Timed out",
                RunConclusion.ActionRequired => "Needs action",
                _ => "Failed",
            },
        };
        Heading = $"{run.WorkflowName} #{run.Number}" + (run.Attempt > 1 ? $" (attempt {run.Attempt})" : "");
        EventText = run.Event switch
        {
            "push" => "push",
            "pull_request" or "pull_request_target" when run.PullRequestNumbers.Count > 0 => $"pull request #{run.PullRequestNumbers[0]}",
            "pull_request" or "pull_request_target" => "pull request",
            "workflow_dispatch" => "run manually",
            "schedule" => "schedule",
            var e => e.Replace('_', ' '),
        };
        StartedLine = $"{actor.Name} started it {DateText.Relative(run.StartedAt ?? run.CreatedAt)} · {EventText}";
        ShortSha = run.HeadSha.Length > 7 ? run.HeadSha[..7] : run.HeadSha;
    }

    public WorkflowRun Run { get; }
    public PrPerson Actor { get; }
    public IBrush RailBrush { get; }
    public string StatusText { get; }
    public string Heading { get; }
    public string Title => Run.Title;
    public bool HasTitle => Run.Title.Length > 0;
    public string Branch => Run.Branch;
    public string ShortSha { get; }
    public string EventText { get; }
    public string StartedLine { get; }

    /// <summary>"3m 12s so far" while running, "took 4m 05s" when done (kept current by the sidebar's clock).</summary>
    [ObservableProperty]
    public partial string DurationText { get; set; } = "";

    public ObservableCollection<RunJobLine> Jobs { get; } = [];

    [ObservableProperty]
    public partial string? JobsMessage { get; set; } = "Loading jobs…";

    /// <summary>Loads the jobs the first time the card is shown.</summary>
    public async void EnsureJobs()
    {
        if (_jobsRequested || _loadJobs is null) return;
        _jobsRequested = true;
        try
        {
            var jobs = await _loadJobs(Run.Id);
            foreach (var j in jobs)
            {
                var rail = WorkflowTriage.Rail(j.Status, j.Conclusion);
                var state = rail switch
                {
                    RunRail.Queued => "queued",
                    RunRail.Running => "running",
                    RunRail.Succeeded => j.Conclusion == RunConclusion.Skipped ? "skipped" : "passed",
                    RunRail.Cancelled => "cancelled",
                    _ => "failed",
                };
                Jobs.Add(new RunJobLine(j.Name, RunRailColors.For(rail), state, rail == RunRail.Running ? j.CurrentStep : null));
            }
            JobsMessage = jobs.Count == 0 ? "No jobs yet" : null;
        }
        catch (HostException ex)
        {
            JobsMessage = ex.Message;
            _jobsRequested = false; // try again next time
        }
    }
}

/// <summary>The workflow rail's colours.</summary>
public static class RunRailColors
{
    public static readonly IBrush Queued = new SolidColorBrush(Color.Parse("#8A9099"));
    public static readonly IBrush Cancelled = new SolidColorBrush(Color.Parse("#4A5059"));

    public static IBrush For(RunRail rail) => rail switch
    {
        RunRail.Running => PrColors.Pending,
        RunRail.Succeeded => PrColors.Success,
        RunRail.Failed => PrColors.Failure,
        RunRail.Cancelled => Cancelled,
        _ => Queued,
    };
}
