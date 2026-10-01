using System.Text.Json;

namespace TotalGit.Core.Hosting.GitHub;

/// <summary>
/// GitHub Actions runs on a github.com repository, through the REST API (Actions isn't in GraphQL). Lists use
/// conditional requests, so polling while runs are going doesn't use up the rate limit.
/// </summary>
public sealed class GitHubWorkflowProvider : IWorkflowProvider
{
    // Active runs are always among the newest, so this covers them unless dozens are queued at once.
    private const int NewestRuns = 50;

    private readonly GitHubHttp _http;
    private readonly string _repoPath;

    internal GitHubWorkflowProvider(RemoteHostInfo host, GitHubHttp http)
    {
        Host = host;
        _http = http;
        _repoPath = $"repos/{Uri.EscapeDataString(host.Owner)}/{Uri.EscapeDataString(host.Repo)}/actions";
    }

    public RemoteHostInfo Host { get; }

    public WorkflowCapabilities Capabilities =>
        WorkflowCapabilities.List | WorkflowCapabilities.Jobs | WorkflowCapabilities.Cancel |
        WorkflowCapabilities.Rerun | WorkflowCapabilities.RerunFailed;

    public async Task<IReadOnlyList<WorkflowRun>> ListRunsAsync(int recentFinished, bool fresh = false, CancellationToken ct = default)
    {
        // One list of the newest runs, whatever their status: asking per status leaves a gap while a run moves from
        // queued to in progress (it's briefly in neither list), and costs a request per status.
        var runs = await Runs($"runs?per_page={NewestRuns}", fresh, ct);
        var active = runs.Where(r => r.IsActive).OrderByDescending(r => r.CreatedAt);
        var finished = runs.Where(r => !r.IsActive).OrderByDescending(r => r.UpdatedAt).Take(recentFinished);
        return [.. active, .. finished];
    }

    private async Task<List<WorkflowRun>> Runs(string query, bool fresh, CancellationToken ct)
    {
        var root = await _http.RestCachedAsync($"{_repoPath}/{query}", ct, fresh);
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("workflow_runs", out var runs)
            ? runs.EnumerateArray().Select(Run).ToList()
            : [];
    }

    public async Task<IReadOnlyList<WorkflowJob>> GetJobsAsync(long runId, CancellationToken ct = default)
    {
        var root = await _http.RestAsync(HttpMethod.Get, $"{_repoPath}/runs/{runId}/jobs?per_page=100", null, ct);
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("jobs", out var jobs)
            ? jobs.EnumerateArray().Select(Job).ToList()
            : [];
    }

    public Task CancelAsync(long runId, CancellationToken ct = default) =>
        _http.RestAsync(HttpMethod.Post, $"{_repoPath}/runs/{runId}/cancel", null, ct);

    public Task RerunAsync(long runId, CancellationToken ct = default) =>
        _http.RestAsync(HttpMethod.Post, $"{_repoPath}/runs/{runId}/rerun", null, ct);

    public Task RerunFailedAsync(long runId, CancellationToken ct = default) =>
        _http.RestAsync(HttpMethod.Post, $"{_repoPath}/runs/{runId}/rerun-failed-jobs", null, ct);

    private static WorkflowRun Run(JsonElement r) => new(
        r.GetProperty("id").GetInt64(),
        r.Int("run_number") ?? 0,
        r.Str("name") ?? "Workflow",
        r.Str("display_title") ?? "",
        r.Str("head_branch") ?? "",
        r.Str("head_sha") ?? "",
        r.Str("event") ?? "",
        r.Obj("triggering_actor") is { ValueKind: JsonValueKind.Object } t ? Person(t)
            : r.Obj("actor") is { ValueKind: JsonValueKind.Object } a ? Person(a) : new PrUser("ghost", null),
        Status(r.Str("status")),
        Conclusion(r.Str("conclusion")),
        Date(r, "created_at") ?? DateTimeOffset.MinValue,
        Date(r, "run_started_at"),
        Date(r, "updated_at") ?? DateTimeOffset.MinValue,
        r.Str("html_url") ?? "",
        r.Int("run_attempt") ?? 1)
    {
        PullRequestNumbers = r.Obj("pull_requests") is { ValueKind: JsonValueKind.Array } prs
            ? prs.EnumerateArray().Select(p => p.Int("number")).OfType<int>().ToList()
            : [],
    };

    private static WorkflowJob Job(JsonElement j)
    {
        var current = j.Obj("steps") is { ValueKind: JsonValueKind.Array } steps
            ? steps.EnumerateArray().FirstOrDefault(s => s.Str("status") == "in_progress") is { ValueKind: JsonValueKind.Object } step ? step.Str("name") : null
            : null;
        return new WorkflowJob(j.Str("name") ?? "", Status(j.Str("status")), Conclusion(j.Str("conclusion")),
            Date(j, "started_at"), Date(j, "completed_at"), j.Str("html_url"), current);
    }

    private static PrUser Person(JsonElement u) => new(u.Str("login") ?? "ghost", u.Str("avatar_url"));

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        e.Obj(name) is { ValueKind: JsonValueKind.String } v && v.TryGetDateTimeOffset(out var d) ? d : null;

    private static RunStatus Status(string? s) => s switch
    {
        "in_progress" => RunStatus.InProgress,
        "waiting" or "pending" or "requested" => RunStatus.Waiting,
        "completed" => RunStatus.Completed,
        _ => RunStatus.Queued,
    };

    private static RunConclusion Conclusion(string? c) => c switch
    {
        "success" => RunConclusion.Success,
        "failure" or "startup_failure" => RunConclusion.Failure,
        "cancelled" => RunConclusion.Cancelled,
        "skipped" => RunConclusion.Skipped,
        "timed_out" => RunConclusion.TimedOut,
        "action_required" => RunConclusion.ActionRequired,
        "neutral" or "stale" => RunConclusion.Neutral,
        _ => RunConclusion.None,
    };
}
