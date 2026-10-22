using System.Text.Json;
using TotalGit.Core.Hosting;
using TotalGit.Core.Hosting.GitHub;
using TotalGit.Core.Tests.Fakes;

namespace TotalGit.Core.Tests;

public class PullRequestNotificationsTests
{
    private const string GraphQL = "https://api.github.com/graphql";

    private sealed class Token(string token) : ICredentialSource
    {
        public string? GetToken(string host) => token;
    }

    private sealed class Accounts(params string[] logins) : IGitHubAccounts
    {
        public Task<IReadOnlyList<GitHubAccount>> ListAsync(string host, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GitHubAccount>>(logins.Select((l, i) => new GitHubAccount(l, i == 0)).ToList());

        public Task<string?> TokenAsync(string host, string login, CancellationToken ct) => Task.FromResult<string?>($"token-{login}");

        public Task<bool> SwitchAsync(string host, string login, CancellationToken ct) => throw new InvalidOperationException("must not switch");
    }

    private const string Response = """
        {"data":{
          "viewer":{"login":"me"},
          "requested":{"nodes":[
            {"number":7,"title":"Add widgets","url":"https://github.com/octo/widgets/pull/7","isDraft":false,"headRefOid":"aaa",
             "repository":{"nameWithOwner":"octo/widgets"},"author":{"__typename":"User","login":"alice"},
             "timelineItems":{"nodes":[{"id":"RQ1","requestedReviewer":{"login":"me"}},{"id":"AS1","assignee":{"login":"me"}},
               {"id":"RQ2","requestedReviewer":{"login":"me"}},{"id":"RQ3","requestedReviewer":{"login":"bob"}},{"id":"RQ4","requestedReviewer":{}}]}},
            {}
          ]},
          "assigned":{"nodes":[
            {"number":7,"title":"Add widgets","url":"https://github.com/octo/widgets/pull/7","isDraft":false,"headRefOid":"aaa",
             "repository":{"nameWithOwner":"octo/widgets"},"author":{"__typename":"User","login":"alice"}},
            {"number":3,"title":"Mine and assigned","url":"https://github.com/me/tools/pull/3","isDraft":false,"headRefOid":"ccc",
             "repository":{"nameWithOwner":"me/tools"},"author":{"__typename":"User","login":"me"}}
          ]},
          "mine":{"nodes":[
            {"number":3,"title":"Mine and assigned","url":"https://github.com/me/tools/pull/3","isDraft":false,"headRefOid":"ccc",
             "repository":{"nameWithOwner":"me/tools"},"author":{"__typename":"User","login":"me"},
             "commits":{"nodes":[{"commit":{"statusCheckRollup":{"state":"FAILURE"}}}]},
             "reviews":{"nodes":[
               {"id":"R1","state":"APPROVED","body":"","author":{"__typename":"User","login":"bob"}},
               {"id":"R2","state":"COMMENTED","body":"","author":{"__typename":"User","login":"carol"}},
               {"id":"R3","state":"COMMENTED","body":"Looks close","author":{"__typename":"User","login":"carol"}},
               {"id":"R4","state":"COMMENTED","body":"Replying","author":{"__typename":"User","login":"me"}}
             ]},
             "comments":{"nodes":[
               {"id":"C1","author":{"__typename":"User","login":"dave"}},
               {"id":"C2","author":{"__typename":"Bot","login":"codecov"}}
             ]},
             "reviewThreads":{"nodes":[{"comments":{"nodes":[
               {"id":"T1","author":{"__typename":"User","login":"carol"}},
               {"id":"T2","author":{"__typename":"User","login":"me"}}
             ]}}]}}
          ]}
        }}
        """;

    private static async Task<(Inbox Inbox, FakeHttpHandler Http)> Fetch(params string[] accounts)
    {
        var http = new FakeHttpHandler();
        http.Route(GraphQL, () => FakeHttpHandler.Json(Response));
        using var factory = new GitHubProviderFactory(new Token("usual"), new HttpClient(http), new Accounts(accounts));
        var inboxes = await factory.CreateInboxesAsync();
        return (await inboxes[0].GetAsync(), http);
    }

    [Fact]
    public async Task Inbox_merges_the_three_searches()
    {
        var (inbox, http) = await Fetch();

        Assert.Equal("me", inbox.Viewer);
        Assert.Equal(2, inbox.PullRequests.Count); // the empty search result is skipped, #7 appears once
        var widgets = inbox.PullRequests.Single(p => p.Number == 7);
        Assert.True(widgets.ReviewRequested);
        Assert.True(widgets.Assigned);
        Assert.False(widgets.IsViewerAuthor);
        Assert.Equal("octo/widgets", widgets.Repository);
        Assert.Equal("RQ2", widgets.ReviewRequestId); // the latest asking the viewer, not bob or a team
        Assert.Equal("AS1", widgets.AssignmentId);

        var mine = inbox.PullRequests.Single(p => p.Number == 3);
        Assert.True(mine.IsViewerAuthor);
        Assert.True(mine.Assigned);
        Assert.Equal(ChecksState.Failure, mine.Checks); // the activity from the "mine" search was kept
        Assert.Equal(4, mine.Reviews.Count);
        Assert.Equal(["C1", "C2", "T1", "T2"], mine.Comments.Select(c => c.Id));

        var variables = JsonDocument.Parse(http.Bodies[0]!).RootElement.GetProperty("variables");
        Assert.Contains("user-review-requested:@me", variables.GetProperty("requested").GetString());
        Assert.Equal("Bearer usual", http.Requests[0].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Each_gh_account_gets_its_own_inbox_and_token()
    {
        var http = new FakeHttpHandler();
        http.Route(GraphQL, () => FakeHttpHandler.Json(Response));
        using var factory = new GitHubProviderFactory(new Token("usual"), new HttpClient(http), new Accounts("work", "home"));

        foreach (var inbox in await factory.CreateInboxesAsync()) await inbox.GetAsync();

        Assert.Equal(["Bearer token-work", "Bearer token-home"], http.Requests.Select(r => r.Headers.Authorization!.ToString()));
    }

    [Fact]
    public async Task Events_leave_out_the_viewers_own_bots_and_empty_reviews()
    {
        var (inbox, _) = await Fetch();

        var ids = PullRequestNotifications.Events(inbox).Select(e => e.Id).Order().ToList();

        // Not R2 (an empty review left by a line comment), R4, T2 (mine) or C2 (a bot).
        string[] expected = ["rr:octo/widgets#7:RQ2", "as:octo/widgets#7:AS1", "as:me/tools#3:", "ap:R1", "cm:R3", "cm:C1", "cm:T1", "cf:me/tools#3:ccc"];
        Assert.Equal(expected.Order(), ids);
    }

    private static InboxPullRequest Pr(int n = 1, string repo = "octo/widgets") =>
        new(repo, n, $"Title {n}", $"https://github.com/{repo}/pull/{n}", new PrUser("alice", null), false, "sha1");

    private static Inbox Mine(params InboxPullRequest[] prs) =>
        new("me", prs.Select(p => p with { IsViewerAuthor = true }).ToList());

    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<InboxEvent> Diff(ref Dictionary<string, DateTimeOffset>? seen, Inbox inbox, DateTimeOffset now)
    {
        var news = PullRequestNotifications.Diff(seen, inbox, now, out var nowSeen);
        seen = nowSeen;
        return news;
    }

    [Fact]
    public void First_poll_is_silent_then_only_news_is_new()
    {
        Dictionary<string, DateTimeOffset>? seen = null;
        Assert.Empty(Diff(ref seen, new Inbox("me", [Pr() with { ReviewRequested = true, ReviewRequestId = "RQ1" }]), T0));

        var news = Diff(ref seen, new Inbox("me", [Pr() with { ReviewRequested = true, ReviewRequestId = "RQ1" }, Pr(2) with { Assigned = true, AssignmentId = "AS1" }]), T0.AddMinutes(2));

        Assert.Equal(["as:octo/widgets#2:AS1"], news.Select(e => e.Id));
    }

    [Fact]
    public void A_pull_request_missing_from_one_poll_isnt_news_when_it_comes_back()
    {
        var asked = new Inbox("me", [Pr() with { ReviewRequested = true, ReviewRequestId = "RQ1" }]);
        Dictionary<string, DateTimeOffset>? seen = null;
        Diff(ref seen, asked, T0);

        Diff(ref seen, new Inbox("me", []), T0.AddMinutes(2)); // GitHub's search left it out

        Assert.Empty(Diff(ref seen, asked, T0.AddMinutes(4)));
    }

    [Fact]
    public void A_pull_request_turning_up_late_brings_only_its_request_not_its_old_activity()
    {
        Dictionary<string, DateTimeOffset>? seen = null;
        Diff(ref seen, Mine(Pr(1)), T0); // the search left #2 out

        var late = Pr(2) with { Comments = [new("C1", new PrUser("bob", null))], Checks = ChecksState.Failure };
        var news = Diff(ref seen, new Inbox("me", [Pr(1) with { IsViewerAuthor = true }, late with { IsViewerAuthor = true },
            Pr(3) with { ReviewRequested = true, ReviewRequestId = "RQ1" }]), T0.AddMinutes(2));
        Assert.Equal(["rr:octo/widgets#3:RQ1"], news.Select(e => e.Id));

        var newComment = late with { IsViewerAuthor = true, Comments = [..late.Comments, new("C2", new PrUser("carol", null))] };
        Assert.Equal(["cm:C2"], Diff(ref seen, Mine(newComment), T0.AddMinutes(4)).Select(e => e.Id));
    }

    [Fact]
    public void Review_requested_again_after_reviewing_is_news_again()
    {
        Dictionary<string, DateTimeOffset>? seen = null;
        Diff(ref seen, new Inbox("me", [Pr() with { ReviewRequested = true, ReviewRequestId = "RQ1" }]), T0);
        Diff(ref seen, new Inbox("me", []), T0.AddMinutes(2)); // reviewed: request gone

        var again = Diff(ref seen, new Inbox("me", [Pr() with { ReviewRequested = true, ReviewRequestId = "RQ2" }]), T0.AddMinutes(4));

        Assert.Single(again);
    }

    [Fact]
    public void Gone_events_are_forgotten_after_a_while()
    {
        Dictionary<string, DateTimeOffset>? seen = null;
        Diff(ref seen, new Inbox("me", [Pr() with { Assigned = true, AssignmentId = "AS1" }]), T0);

        Diff(ref seen, new Inbox("me", []), T0 + PullRequestNotifications.Remember);

        Assert.Empty(seen!);
    }

    [Fact]
    public void Failed_checks_are_news_once_per_commit()
    {
        Dictionary<string, DateTimeOffset>? seen = null;
        Diff(ref seen, Mine(Pr()), T0);
        var failing = Mine(Pr() with { Checks = ChecksState.Failure });
        Assert.Single(Diff(ref seen, failing, T0.AddMinutes(1)));

        Assert.Empty(Diff(ref seen, failing, T0.AddMinutes(2)));
        var pushedAndFailed = Mine(Pr() with { Checks = ChecksState.Failure, HeadSha = "sha2" });
        Assert.Single(Diff(ref seen, pushedAndFailed, T0.AddMinutes(4)));
    }

    [Fact]
    public void Approvals_and_comments_only_count_on_the_viewers_own_pull_requests()
    {
        var reviewed = Pr() with { Reviews = [new("R1", ReviewState.Approved, new PrUser("bob", null), false)], Comments = [new("C1", new PrUser("bob", null))] };

        Assert.Empty(PullRequestNotifications.Events(new Inbox("me", [reviewed with { Assigned = false }])));
        Assert.Equal(2, PullRequestNotifications.Events(Mine(reviewed)).Count);
    }

    [Fact]
    public void One_notification_per_pull_request_with_the_most_important_news_first()
    {
        var pr = Pr(42) with { Title = "Fix login" };
        var events = new List<InboxEvent>
        {
            new("cm:1", InboxEventKind.Commented, pr, new PrUser("bob", null)),
            new("cm:2", InboxEventKind.Commented, pr, new PrUser("carol", null)),
            new("ap:1", InboxEventKind.Approved, pr, new PrUser("alice", null)),
        };

        var n = Assert.Single(PullRequestNotifications.Group(events));

        Assert.Equal("Alice approved, bob and carol left 2 comments", n.Title);
        Assert.Equal("#42 Fix login", n.Text);
        Assert.Equal("octo/widgets", n.Attribution);
        Assert.Equal(pr.Url, n.Url);
    }

    [Theory]
    [InlineData("https://github.com/octo/widgets/pull/42", "octo/widgets", 42)]
    [InlineData("https://github.com/octo/widgets/pull/42/files", "octo/widgets", 42)]
    [InlineData("https://github.com/octo/widgets/issues/42", null, 0)]
    [InlineData("not a url", null, 0)]
    public void Pull_request_addresses_give_their_repository_and_number(string url, string? repository, int number)
    {
        var parsed = PullRequestNotifications.ParseUrl(url);

        Assert.Equal(repository, parsed?.Repository);
        Assert.Equal(number, parsed?.Number ?? 0);
    }

    [Fact]
    public void Many_pull_requests_at_once_make_one_summary()
    {
        var events = Enumerable.Range(1, 6).Select(i => new InboxEvent($"as:{i}", InboxEventKind.Assigned, Pr(i))).ToList();

        var n = Assert.Single(PullRequestNotifications.Group(events));

        Assert.Equal("6 pull requests have news", n.Title);
        Assert.Null(n.Url);
        Assert.StartsWith("#1 Title 1: you were assigned\n", n.Text);
        Assert.EndsWith("…and 3 more", n.Text);
        Assert.Equal(6, n.Events.Count);
    }
}
