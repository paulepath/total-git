namespace TotalGit.Core.Hosting;

/// <summary>Where a workflow run (or job) is in its life.</summary>
public enum RunStatus
{
    Queued,

    /// <summary>Held at an approval gate or a concurrency limit.</summary>
    Waiting,
    InProgress,
    Completed,
}

/// <summary>How a completed run or job ended (None while it's still going).</summary>
public enum RunConclusion
{
    None,
    Success,
    Failure,
    Cancelled,
    Skipped,
    TimedOut,
    ActionRequired,
    Neutral,
}

/// <summary>One run of a CI workflow (a GitHub Actions run; an Azure Pipelines build later).</summary>
/// <param name="Number">The run's number within its workflow ("#128").</param>
/// <param name="Title">What the run is for: the commit message or pull request title.</param>
/// <param name="Event">What started it: push, pull_request, workflow_dispatch, schedule…</param>
/// <param name="Attempt">1 for the first try, higher after re-runs.</param>
public sealed record WorkflowRun(
    long Id,
    int Number,
    string WorkflowName,
    string Title,
    string Branch,
    string HeadSha,
    string Event,
    PrUser Actor,
    RunStatus Status,
    RunConclusion Conclusion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt,
    string Url,
    int Attempt)
{
    /// <summary>Pull requests the run belongs to (for pull_request events).</summary>
    public IReadOnlyList<int> PullRequestNumbers { get; init; } = [];

    public bool IsActive => Status != RunStatus.Completed;
}

/// <summary>One job of a run, with the step it's on while running.</summary>
public sealed record WorkflowJob(
    string Name,
    RunStatus Status,
    RunConclusion Conclusion,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Url,
    string? CurrentStep);

/// <summary>What a workflow provider supports; the app hides actions it can't do.</summary>
[Flags]
public enum WorkflowCapabilities
{
    None = 0,
    List = 1 << 0,
    Jobs = 1 << 1,
    Cancel = 1 << 2,
    Rerun = 1 << 3,
    RerunFailed = 1 << 4,
}

/// <summary>A run's state as one colour on the left of its row.</summary>
public enum RunRail
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public static class WorkflowTriage
{
    public static RunRail Rail(RunStatus status, RunConclusion conclusion) => status switch
    {
        RunStatus.Queued or RunStatus.Waiting => RunRail.Queued,
        RunStatus.InProgress => RunRail.Running,
        _ => conclusion switch
        {
            RunConclusion.Success or RunConclusion.Neutral or RunConclusion.Skipped => RunRail.Succeeded,
            RunConclusion.Cancelled => RunRail.Cancelled,
            _ => RunRail.Failed,
        },
    };

    public static RunRail Rail(WorkflowRun run) => Rail(run.Status, run.Conclusion);
}
