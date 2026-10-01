using System.Net;
using TotalGit.Core.Hosting;
using TotalGit.Core.Hosting.GitHub;
using TotalGit.Core.Tests.Fakes;

namespace TotalGit.Core.Tests;

public sealed class GitHubWorkflowProviderTests
{
    private const string Runs = "https://api.github.com/repos/octo/widgets/actions/runs";
    private static readonly RemoteHostInfo Repo = new(HostKind.GitHub, "github.com", "octo", "widgets", "origin");

    private sealed class Token : ICredentialSource
    {
        public string? GetToken(string host) => "t";
    }

    private static (IWorkflowProvider Provider, FakeHttpHandler Http) Create()
    {
        var http = new FakeHttpHandler();
        var factory = new GitHubProviderFactory(new Token(), new HttpClient(http));
        return (factory.TryCreateWorkflows(Repo)!, http);
    }

    private static string Run(long id, string status, string? conclusion, string created, string name = "CI", string branch = "main") => $$"""
        {"id":{{id}},"run_number":{{id}},"name":"{{name}}","display_title":"Fix login","head_branch":"{{branch}}","head_sha":"abc{{id}}",
         "event":"push","status":"{{status}}","conclusion":{{(conclusion is null ? "null" : $"\"{conclusion}\"")}},
         "created_at":"{{created}}","run_started_at":"{{created}}","updated_at":"{{created}}","html_url":"https://github.com/octo/widgets/actions/runs/{{id}}",
         "run_attempt":1,"actor":{"login":"alice","avatar_url":"https://a/alice"},"triggering_actor":{"login":"bob","avatar_url":"https://a/bob"},
         "pull_requests":[{"number":7}]}
        """;

    private static HttpResponseMessage List(params string[] runs) =>
        FakeHttpHandler.Json($$"""{"total_count":{{runs.Length}},"workflow_runs":[{{string.Join(",", runs)}}]}""");

    [Fact]
    public void Factory_only_serves_github_com()
    {
        using var factory = new GitHubProviderFactory(new Token(), new HttpClient(new FakeHttpHandler()));
        Assert.IsType<GitHubWorkflowProvider>(factory.TryCreateWorkflows(Repo));
        Assert.Null(factory.TryCreateWorkflows(Repo with { Host = "github.example.com" }));
        Assert.Null(factory.TryCreateWorkflows(new RemoteHostInfo(HostKind.AzureDevOps, "dev.azure.com", "org/proj", "r", "origin")));
    }

    [Fact]
    public async Task Lists_active_runs_newest_first_then_recent_finished_ones()
    {
        var (provider, http) = Create();
        http.Route(Runs + "?per_page=50", () => List(
            Run(6, "queued", null, "2026-10-01T12:00:00Z"),
            Run(5, "in_progress", null, "2026-10-01T11:00:00Z"),
            Run(4, "pending", null, "2026-10-01T10:30:00Z"),
            Run(3, "in_progress", null, "2026-10-01T10:00:00Z"),
            Run(2, "completed", "failure", "2026-10-01T09:00:00Z"),
            Run(1, "completed", "success", "2026-10-01T08:00:00Z"),
            Run(0, "completed", "success", "2026-10-01T07:00:00Z")));

        var runs = await provider.ListRunsAsync(2);

        Assert.Equal([6, 5, 4, 3, 2, 1], runs.Select(r => r.Id));
        Assert.Single(http.Requests);
        var first = runs[0];
        Assert.Equal(("CI", "Fix login", "main", "abc6", "push"), (first.WorkflowName, first.Title, first.Branch, first.HeadSha, first.Event));
        Assert.Equal(new PrUser("bob", "https://a/bob"), first.Actor); // who started this attempt
        Assert.Equal(RunStatus.Queued, first.Status);
        Assert.Equal(RunStatus.Waiting, runs[2].Status);
        Assert.Equal([7], first.PullRequestNumbers);
        Assert.Equal((RunStatus.Completed, RunConclusion.Failure), (runs[4].Status, runs[4].Conclusion));
    }

    [Fact]
    public async Task Unchanged_lists_are_served_from_the_last_response()
    {
        var (provider, http) = Create();
        var calls = 0;
        http.Routes[Runs + "?per_page=50"] = req =>
        {
            calls++;
            if (req.Headers.IfNoneMatch.Any(e => e.Tag == "\"v1\"")) return new HttpResponseMessage(HttpStatusCode.NotModified);
            var response = List(Run(3, "in_progress", null, "2026-10-01T10:00:00Z"));
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return response;
        };

        Assert.Equal([3], (await provider.ListRunsAsync(0)).Select(r => r.Id));
        Assert.Equal([3], (await provider.ListRunsAsync(0)).Select(r => r.Id));
        Assert.Equal(2, calls);
        Assert.Single(http.Requests[1].Headers.IfNoneMatch);

        // Just after cancelling or re-running, ask without the ETag.
        await provider.ListRunsAsync(0, fresh: true);
        Assert.Empty(http.Requests[2].Headers.IfNoneMatch);
    }

    [Fact]
    public async Task Jobs_report_the_step_they_are_on()
    {
        var (provider, http) = Create();
        http.Route(Runs + "/5/jobs", () => FakeHttpHandler.Json("""
            {"jobs":[
              {"name":"build","status":"completed","conclusion":"success","started_at":"2026-10-01T10:00:00Z","completed_at":"2026-10-01T10:01:00Z","html_url":"u1",
               "steps":[{"name":"Checkout","status":"completed"}]},
              {"name":"tests","status":"in_progress","conclusion":null,"started_at":"2026-10-01T10:01:00Z","completed_at":null,"html_url":"u2",
               "steps":[{"name":"Checkout","status":"completed"},{"name":"Run tests","status":"in_progress"},{"name":"Upload","status":"queued"}]}]}
            """));

        var jobs = await provider.GetJobsAsync(5);

        Assert.Equal(["build", "tests"], jobs.Select(j => j.Name));
        Assert.Equal((RunStatus.Completed, RunConclusion.Success, (string?)null), (jobs[0].Status, jobs[0].Conclusion, jobs[0].CurrentStep));
        Assert.Equal((RunStatus.InProgress, "Run tests"), (jobs[1].Status, jobs[1].CurrentStep));
    }

    [Fact]
    public async Task Cancel_and_reruns_post_to_their_endpoints()
    {
        var (provider, http) = Create();
        http.Route(Runs + "/9/", () => new HttpResponseMessage(HttpStatusCode.Accepted));

        await provider.CancelAsync(9);
        await provider.RerunAsync(9);
        await provider.RerunFailedAsync(9);

        Assert.All(http.Requests, r => Assert.Equal(HttpMethod.Post, r.Method));
        Assert.Equal(["/9/cancel", "/9/rerun", "/9/rerun-failed-jobs"],
            http.Requests.Select(r => r.RequestUri!.AbsolutePath[r.RequestUri.AbsolutePath.LastIndexOf("/9/", StringComparison.Ordinal)..]));
    }

    [Fact]
    public async Task A_token_without_actions_access_gets_a_message_saying_so()
    {
        var (provider, http) = Create();
        http.Route(Runs + "/9/cancel", () => FakeHttpHandler.Json("""{"message":"Resource not accessible by personal access token"}""", HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<HostException>(() => provider.CancelAsync(9));

        Assert.Contains("Actions access", ex.Message);
    }

    [Theory]
    [InlineData(RunStatus.Queued, RunConclusion.None, RunRail.Queued)]
    [InlineData(RunStatus.Waiting, RunConclusion.None, RunRail.Queued)]
    [InlineData(RunStatus.InProgress, RunConclusion.None, RunRail.Running)]
    [InlineData(RunStatus.Completed, RunConclusion.Success, RunRail.Succeeded)]
    [InlineData(RunStatus.Completed, RunConclusion.Skipped, RunRail.Succeeded)]
    [InlineData(RunStatus.Completed, RunConclusion.Cancelled, RunRail.Cancelled)]
    [InlineData(RunStatus.Completed, RunConclusion.Failure, RunRail.Failed)]
    [InlineData(RunStatus.Completed, RunConclusion.TimedOut, RunRail.Failed)]
    public void Rail_colours_follow_status_and_conclusion(RunStatus status, RunConclusion conclusion, RunRail rail)
    {
        Assert.Equal(rail, WorkflowTriage.Rail(status, conclusion));
    }
}
