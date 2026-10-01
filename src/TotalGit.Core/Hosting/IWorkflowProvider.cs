namespace TotalGit.Core.Hosting;

/// <summary>
/// CI workflow runs on one repository of one hosting service (GitHub Actions now; Azure Pipelines can implement
/// the same). Failures are <see cref="HostException"/>s with messages fit to show.
/// </summary>
public interface IWorkflowProvider
{
    RemoteHostInfo Host { get; }
    WorkflowCapabilities Capabilities { get; }

    /// <summary>Queued, waiting and running runs, newest first, then the <paramref name="recentFinished"/> latest finished ones.</summary>
    /// <param name="fresh">Skip any cached answer (just after cancelling or re-running, when the host may still say "unchanged").</param>
    Task<IReadOnlyList<WorkflowRun>> ListRunsAsync(int recentFinished, bool fresh = false, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowJob>> GetJobsAsync(long runId, CancellationToken ct = default);

    Task CancelAsync(long runId, CancellationToken ct = default);

    /// <summary>Runs the whole workflow again.</summary>
    Task RerunAsync(long runId, CancellationToken ct = default);

    /// <summary>Runs only the jobs that failed (and those depending on them) again.</summary>
    Task RerunFailedAsync(long runId, CancellationToken ct = default);
}

/// <summary>Creates the workflow provider for a repository's host; null when the host isn't supported.</summary>
public interface IWorkflowProviderFactory
{
    IWorkflowProvider? TryCreateWorkflows(RemoteHostInfo host);
}
