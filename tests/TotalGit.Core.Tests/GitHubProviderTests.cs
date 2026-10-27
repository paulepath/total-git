using System.Net;
using System.Text.Json;
using TotalGit.Core.Hosting;
using TotalGit.Core.Hosting.GitHub;
using TotalGit.Core.Tests.Fakes;

namespace TotalGit.Core.Tests;

public class GitHubProviderTests
{
    private const string GraphQL = "https://api.github.com/graphql";
    private const string Rest = "https://api.github.com/repos/octo/widgets";
    private static readonly RemoteHostInfo Repo = new(HostKind.GitHub, "github.com", "octo", "widgets", "origin");

    private sealed class Token(string? token) : ICredentialSource
    {
        public string? GetToken(string host) => host == "github.com" ? token : null;
    }

    private static (IPullRequestProvider Provider, FakeHttpHandler Http) Create(string? token = "t0ken")
    {
        var http = new FakeHttpHandler();
        var factory = new GitHubProviderFactory(new Token(token), new HttpClient(http));
        return (factory.TryCreate(Repo)!, http);
    }

    private static JsonElement Body(FakeHttpHandler http, int index = 0) => JsonDocument.Parse(http.Bodies[index]!).RootElement;

    [Fact]
    public void Factory_only_serves_github_com()
    {
        using var factory = new GitHubProviderFactory(new Token("t"), new HttpClient(new FakeHttpHandler()));
        Assert.IsType<GitHubPullRequestProvider>(factory.TryCreate(Repo));
        Assert.Null(factory.TryCreate(Repo with { Host = "github.example.com" }));
        Assert.Null(factory.TryCreate(new RemoteHostInfo(HostKind.AzureDevOps, "dev.azure.com", "org/proj", "r", "origin")));
        var provider = factory.TryCreate(Repo)!;
        Assert.Equal("refs/pull/12/head", provider.HeadRefSpec(12));
        Assert.True(provider.Capabilities.HasFlag(PrCapabilities.ResolveThreads | PrCapabilities.RequestChanges | PrCapabilities.Drafts));
    }

    // ---- Reads -------------------------------------------------------------------------------------------------

    private const string ListJson = """
        {"data":{"viewer":{"login":"Me"},"repository":{"pullRequests":{"nodes":[
          {"number":7,"title":"Draft work","isDraft":true,"state":"OPEN","baseRefName":"main","headRefName":"feature/x",
           "headRefOid":"aaa111","isCrossRepository":false,"updatedAt":"2026-09-30T10:00:00Z","url":"https://github.com/octo/widgets/pull/7",
           "reviewDecision":"CHANGES_REQUESTED","author":{"login":"alice","avatarUrl":"https://a/alice","name":"Alice Archer"},
           "reviewRequests":{"nodes":[{"requestedReviewer":null},{"requestedReviewer":{"__typename":"Team","name":"core","avatarUrl":null}},
                                      {"requestedReviewer":{"__typename":"User","login":"me","avatarUrl":"https://a/me","name":"Me Myself"}}]},
           "commits":{"nodes":[{"commit":{"statusCheckRollup":{"state":"FAILURE","contexts":{"nodes":[
             {"__typename":"CheckRun","name":"build","status":"COMPLETED","conclusion":"SUCCESS","detailsUrl":null,"title":null},
             {"__typename":"CheckRun","name":"api-tests","status":"COMPLETED","conclusion":"FAILURE","detailsUrl":null,"title":null}]}}}}]}},
          {"number":5,"title":"From a fork","isDraft":false,"state":"OPEN","baseRefName":"main","headRefName":"patch-1",
           "headRefOid":"bbb222","isCrossRepository":true,"updatedAt":"2026-09-29T09:00:00Z","url":"https://github.com/octo/widgets/pull/5",
           "reviewDecision":null,"author":null,
           "reviewRequests":{"nodes":[{"requestedReviewer":{"__typename":"Team","name":"Me","avatarUrl":null}}]},
           "commits":{"nodes":[{"commit":{"statusCheckRollup":null}}]}},
          {"number":3,"title":"Approved","isDraft":false,"state":"OPEN","baseRefName":"main","headRefName":"b","headRefOid":"ccc",
           "isCrossRepository":false,"updatedAt":"2026-09-28T09:00:00Z","url":"u","reviewDecision":"APPROVED",
           "author":{"login":"bob","avatarUrl":null},"reviewRequests":{"nodes":[]},
           "createdAt":"2026-09-20T09:00:00Z","additions":10,"deletions":2,"changedFiles":3,"mergeable":"CONFLICTING","mergeStateStatus":"DIRTY",
           "latestReviews":{"nodes":[{"state":"APPROVED","author":{"login":"carol","avatarUrl":"https://a/carol","name":"Carol Cole"}}]},
           "commits":{"nodes":[{"commit":{"statusCheckRollup":{"state":"EXPECTED"}}}]}}
        ]}}}}
        """;

    [Fact]
    public void Every_graphql_document_has_balanced_brackets()
    {
        // A missing brace only shows up as a parse error from GitHub, so check the documents here.
        var queries = typeof(GitHubPullRequestProvider).Assembly.GetType("TotalGit.Core.Hosting.GitHub.GitHubQueries")!
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToList();
        Assert.NotEmpty(queries);
        foreach (var field in queries)
        {
            var text = (string)field.GetRawConstantValue()!;
            Assert.True(text.Count(c => c == '{') == text.Count(c => c == '}'), $"{field.Name}: unbalanced braces");
            Assert.True(text.Count(c => c == '(') == text.Count(c => c == ')'), $"{field.Name}: unbalanced parentheses");
        }
    }

    [Fact]
    public async Task Lists_open_pull_requests()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => FakeHttpHandler.Json(ListJson));

        var list = await provider.ListOpenAsync();

        Assert.Equal([7, 5, 3], list.Select(p => p.Number));
        var draft = list[0];
        Assert.True(draft.IsDraft);
        Assert.Equal(new PrUser("alice", "https://a/alice", "Alice Archer"), draft.Author);
        Assert.Equal(("Alice Archer", "Alice Archer (alice)"), (draft.Author.DisplayName, draft.Author.NameAndLogin));
        Assert.Equal(("main", "feature/x", "aaa111"), (draft.BaseRef, draft.HeadRef, draft.HeadSha));
        Assert.Equal(ChecksState.Failure, draft.Checks);
        Assert.Equal(ReviewDecision.ChangesRequested, draft.ReviewDecision);
        Assert.True(draft.ViewerReviewRequested); // login compared case-insensitively
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero), draft.UpdatedAt);

        var fork = list[1];
        Assert.True(fork.IsCrossRepository);
        Assert.Equal(ChecksState.None, fork.Checks);
        Assert.Equal(ReviewDecision.None, fork.ReviewDecision);
        Assert.False(fork.ViewerReviewRequested); // a team with the viewer's name isn't the viewer
        Assert.Equal("ghost", fork.Author.Login);
        Assert.Equal(("bob", "bob"), (list[2].Author.DisplayName, list[2].Author.NameAndLogin)); // no name set: the login

        Assert.Equal(ReviewDecision.Approved, list[2].ReviewDecision);
        Assert.Equal(ChecksState.Pending, list[2].Checks);

        // Who it's for: requested users and teams, then people who reviewed without being asked.
        Assert.Equal(["core", "me"], draft.Reviewers.Select(r => r.Name));
        Assert.All(draft.Reviewers, r => Assert.True(r.IsRequested));
        Assert.Equal([new Reviewer("carol", "https://a/carol", ReviewState.Approved, IsRequested: false) { FullName = "Carol Cole" }], list[2].Reviewers);
        Assert.Equal(["core", "Me Myself"], draft.Reviewers.Select(r => r.DisplayName)); // a team has no person's name

        // Extra detail for the hover card.
        Assert.Equal(["build", "api-tests"], draft.CheckRuns.Select(c => c.Name));
        Assert.Equal(CheckStatus.Failure, draft.CheckRuns[1].Status);
        Assert.Equal((10, 2, 3), (list[2].Additions, list[2].Deletions, list[2].ChangedFiles));
        Assert.Equal(MergeState.Conflicting, list[2].MergeState);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero), list[2].CreatedAt);
        Assert.Null(draft.Additions);
        Assert.All(list, p => Assert.False(p.IsViewerAuthor));

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer t0ken", request.Headers.Authorization?.ToString());
        Assert.Contains("TotalGit", request.Headers.UserAgent.ToString());
        var variables = Body(http).GetProperty("variables");
        Assert.Equal("octo", variables.GetProperty("owner").GetString());
        Assert.Equal("widgets", variables.GetProperty("repo").GetString());
        Assert.Equal("repo:octo/widgets is:pr is:open review-requested:@me", variables.GetProperty("requested").GetString());
    }

    [Fact]
    public async Task Waiting_for_you_covers_team_requests_and_reviews_without_approval()
    {
        static string Pr(int n, string reviews, string opinions = "", string author = "alice") => $$$"""
            {"number":{{{n}}},"title":"t","isDraft":false,"state":"OPEN","baseRefName":"main","headRefName":"b{{{n}}}","headRefOid":"x",
             "isCrossRepository":false,"updatedAt":"2026-10-26T10:00:00Z","url":"u{{{n}}}","reviewDecision":"REVIEW_REQUIRED",
             "author":{"login":"{{{author}}}"},"reviewRequests":{"nodes":[]},
             "latestReviews":{"nodes":[{{{reviews}}}]},"latestOpinionatedReviews":{"nodes":[{{{opinions}}}]}}
            """;
        static string Review(string state, string who = "me") => $$$"""{"state":"{{{state}}}","author":{"login":"{{{who}}}"}}""";
        var prs = string.Join(",", Pr(10, ""), Pr(11, Review("COMMENTED")), Pr(12, Review("CHANGES_REQUESTED"), Review("CHANGES_REQUESTED")),
            Pr(13, Review("DISMISSED")), Pr(14, Review("COMMENTED"), Review("APPROVED")), Pr(15, Review("APPROVED"), Review("APPROVED")),
            Pr(16, Review("COMMENTED", "bob")), Pr(17, Review("COMMENTED"), author: "me"));
        var json = """{"data":{"viewer":{"login":"Me"},"requested":{"nodes":[{"number":10},{}]},"repository":{"pullRequests":{"nodes":[PRS]}}}}"""
            .Replace("PRS", prs);
        var (provider, http) = Create();
        http.Route(GraphQL, () => FakeHttpHandler.Json(json));

        var list = await provider.ListOpenAsync();

        // 10: asked through a team (the search found it); 11-13: commented, asked for changes, approval dismissed.
        // Not 14 (approved, then commented), 15 (approved), 16 (only someone else reviewed) or 17 (my own).
        Assert.Equal([10, 11, 12, 13], list.Where(p => p.WaitsForViewer).Select(p => p.Number));
        Assert.True(list[0].ViewerReviewRequested);
    }

    [Fact]
    public async Task Only_the_newest_run_of_a_workflow_counts()
    {
        // PR CI ran twice on the commit (run 254, then 255): the first run's failures and the job the second run
        // cancelled in it are not checks of the pull request any more. CodeRabbit (not a workflow) always counts.
        static string Run(string name, string conclusion, long run) =>
            """{"__typename":"CheckRun","name":"NAME","status":"COMPLETED","conclusion":"CONCLUSION","checkSuite":{"workflowRun":{"databaseId":RUN,"workflow":{"name":"PR CI"}}}}"""
                .Replace("NAME", name).Replace("CONCLUSION", conclusion).Replace("RUN", run.ToString());
        const string rabbit = """{"__typename":"CheckRun","name":"CodeRabbit","status":"COMPLETED","conclusion":"SUCCESS","checkSuite":{"workflowRun":null}}""";
        var contexts = string.Join(",", Run("branch-policy", "FAILURE", 254), Run("api", "CANCELLED", 254),
            Run("branch-policy", "FAILURE", 255), Run("api", "SUCCESS", 255), rabbit);
        var json = """
            {"data":{"viewer":{"login":"Me"},"repository":{"pullRequests":{"nodes":[
              {"number":93,"title":"Fix","isDraft":false,"state":"OPEN","baseRefName":"main","headRefName":"bug/x","headRefOid":"aaa",
               "isCrossRepository":false,"updatedAt":"2026-10-01T10:00:00Z","url":"u","reviewDecision":null,"author":null,
               "reviewRequests":{"nodes":[]},
               "commits":{"nodes":[{"commit":{"statusCheckRollup":{"state":"FAILURE","contexts":{"nodes":[CONTEXTS]}}}}]}}
            ]}}}}
            """.Replace("CONTEXTS", contexts);
        var (provider, http) = Create();
        http.Route(GraphQL, () => FakeHttpHandler.Json(json));

        var checks = (await provider.ListOpenAsync())[0].CheckRuns;

        Assert.Equal([("branch-policy", CheckStatus.Failure), ("api", CheckStatus.Success), ("CodeRabbit", CheckStatus.Success)],
            checks.Select(c => (c.Name, c.Status)));
    }

    [Fact]
    public async Task Caches_the_viewer()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => FakeHttpHandler.Json("""{"data":{"viewer":{"login":"me"}}}"""));
        Assert.Equal("me", await provider.GetViewerAsync());
        Assert.Equal("me", await provider.GetViewerAsync());
        Assert.Single(http.Requests);
    }

    private const string DetailsJson = """
        {"data":{"viewer":{"login":"me"},"repository":{"pullRequest":{
          "number":7,"title":"Widgets","isDraft":false,"state":"OPEN","baseRefName":"main","headRefName":"feature/x",
          "headRefOid":"head1","baseRefOid":"base1","isCrossRepository":false,"updatedAt":"2026-09-30T10:00:00Z","url":"u",
          "reviewDecision":"REVIEW_REQUIRED","author":{"login":"alice","avatarUrl":null},
          "body":"Adds widgets.","mergeStateStatus":"BEHIND","mergeable":"MERGEABLE","additions":11,"deletions":9,"changedFiles":4,
          "viewerDidAuthor":false,
          "reviewRequests":{"nodes":[{"requestedReviewer":{"__typename":"User","login":"carol","avatarUrl":"https://a/carol"}},
                                     {"requestedReviewer":{"__typename":"Team","name":"Core team","avatarUrl":"https://a/team"}}]},
          "latestReviews":{"nodes":[{"state":"APPROVED","author":{"login":"bob","avatarUrl":"https://a/bob"}},
                                    {"state":"COMMENTED","author":{"login":"carol","avatarUrl":"https://a/carol"}}]},
          "commits":{"totalCount":3,"nodes":[{"commit":{"statusCheckRollup":{"state":"PENDING","contexts":{"nodes":[
            {"__typename":"CheckRun","name":"build","status":"COMPLETED","conclusion":"FAILURE","detailsUrl":"https://ci/build","title":"2 errors","isRequired":true},
            {"__typename":"CheckRun","name":"lint","status":"IN_PROGRESS","conclusion":null,"detailsUrl":null,"title":null,"isRequired":false},
            {"__typename":"CheckRun","name":"docs","status":"COMPLETED","conclusion":"SKIPPED","detailsUrl":null,"title":null,"isRequired":false},
            {"__typename":"StatusContext","context":"ci/legacy","state":"SUCCESS","targetUrl":"https://ci/legacy","description":"All good","isRequired":true}
          ]}}}}]},
          "reviewThreads":{"nodes":[
            {"id":"T1","isResolved":false,"isOutdated":true,"path":"src/a.py","line":null,"startLine":null,"originalLine":6,"originalStartLine":null,
             "diffSide":"RIGHT","viewerCanReply":true,"viewerCanResolve":true,"viewerCanUnresolve":false,
             "comments":{"nodes":[
               {"id":"C1","body":"Outdated?","createdAt":"2026-09-30T08:00:00Z","author":{"login":"bob","avatarUrl":null},"commit":{"oid":"old1"},"originalCommit":{"oid":"old1"}},
               {"id":"C2","body":"Yes","createdAt":"2026-09-30T08:05:00Z","author":null,"commit":{"oid":"old1"},"originalCommit":{"oid":"old1"}}]}},
            {"id":"T2","isResolved":false,"isOutdated":false,"path":"src/gone.py","line":2,"startLine":2,"originalLine":2,"originalStartLine":null,
             "diffSide":"LEFT","viewerCanReply":true,"viewerCanResolve":false,"viewerCanUnresolve":false,
             "comments":{"nodes":[{"id":"C3","body":"Removed?","createdAt":"2026-09-30T08:10:00Z","author":{"login":"carol","avatarUrl":null},"commit":{"oid":"head1"},"originalCommit":{"oid":"first1"}}]}},
            {"id":"T3","isResolved":true,"isOutdated":false,"path":"src/b.py","line":5,"startLine":3,"originalLine":5,"originalStartLine":3,
             "diffSide":"RIGHT","viewerCanReply":true,"viewerCanResolve":false,"viewerCanUnresolve":true,
             "comments":{"nodes":[{"id":"C4","body":"Range","createdAt":"2026-09-30T08:20:00Z","author":{"login":"bob","avatarUrl":null},"commit":{"oid":"head1"},"originalCommit":{"oid":"head1"}}]}}
          ]},
          "comments":{"nodes":[
            {"id":"IC2","body":"Later comment","createdAt":"2026-09-30T12:00:00Z","author":{"login":"alice","avatarUrl":null}},
            {"id":"IC1","body":"First comment","createdAt":"2026-09-30T09:00:00Z","author":{"login":"bob","avatarUrl":null}}]},
          "reviews":{"nodes":[
            {"id":"R1","body":"","state":"COMMENTED","submittedAt":"2026-09-30T08:00:00Z","createdAt":"2026-09-30T08:00:00Z","author":{"login":"bob","avatarUrl":null}},
            {"id":"R2","body":"","state":"APPROVED","submittedAt":"2026-09-30T11:00:00Z","createdAt":"2026-09-30T10:30:00Z","author":{"login":"bob","avatarUrl":null}},
            {"id":"R3","body":"Some thoughts","state":"COMMENTED","submittedAt":"2026-09-30T10:00:00Z","createdAt":"2026-09-30T10:00:00Z","author":{"login":"carol","avatarUrl":null}},
            {"id":"R4","body":"Not sent yet","state":"PENDING","submittedAt":null,"createdAt":"2026-09-30T10:05:00Z","author":{"login":"me","avatarUrl":null}}]}
        }}}}
        """;

    [Fact]
    public async Task Reads_viewed_files_across_pages_and_the_viewers_last_review()
    {
        var (provider, http) = Create();
        var details = DetailsJson
            .Replace("\"viewerDidAuthor\":false,", """
                "viewerDidAuthor":false,"id":"PR_node",
                "files":{"nodes":[{"path":"src/a.py","viewerViewedState":"VIEWED"},{"path":"src/b.py","viewerViewedState":"DISMISSED"}],
                         "pageInfo":{"hasNextPage":true,"endCursor":"c1"}},
                """)
            .Replace("""{"id":"R4","body":"Not sent yet","state":"PENDING",""",
                """{"id":"R5","body":"","state":"COMMENTED","submittedAt":"2026-09-30T12:00:00Z","createdAt":"2026-09-30T12:00:00Z","author":{"login":"me","avatarUrl":null},"commit":{"oid":"reviewed1"}},{"id":"R4","body":"Not sent yet","state":"PENDING",""");
        const string page2 = """
            {"data":{"repository":{"pullRequest":{"files":{"nodes":[{"path":"src/c.py","viewerViewedState":"UNVIEWED"},{"path":"src/d.py","viewerViewedState":"VIEWED"}],
              "pageInfo":{"hasNextPage":false,"endCursor":null}}}}}}
            """;
        var call = 0;
        http.Routes[GraphQL] = _ => FakeHttpHandler.Json(call++ == 0 ? details : page2);

        var pr = await provider.GetPullRequestAsync(7);

        Assert.Equal("PR_node", pr.NodeId);
        Assert.Equal(FileViewState.Viewed, pr.ViewedFiles["src/a.py"]);
        Assert.Equal(FileViewState.ChangedSinceViewed, pr.ViewedFiles["src/b.py"]);
        Assert.Equal(FileViewState.Unviewed, pr.ViewedFiles["src/c.py"]);
        Assert.Equal(FileViewState.Viewed, pr.ViewedFiles["src/d.py"]);
        Assert.Equal("c1", Body(http, 1).GetProperty("variables").GetProperty("after").GetString());
        // The pending review isn't submitted, so the last review is R5.
        Assert.Equal("reviewed1", pr.LastViewerReviewSha);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero), pr.LastViewerReviewAt);
    }

    [Fact]
    public async Task An_empty_answer_from_GitHub_is_an_error_not_a_crash()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("") });

        var ex = await Assert.ThrowsAsync<HostException>(() => provider.SetFileViewedAsync(7, "src/a.py", viewed: true));

        Assert.Contains("empty response", ex.Message);
    }

    // What .NET throws when the network drops a connection: a message that says nothing, and the reason inside.
    private static HttpRequestException Dropped() => new("An error occurred while sending the request.",
        new IOException("Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.",
            new System.Net.Sockets.SocketException(10054)));

    private const string IdJson = """{"data":{"repository":{"pullRequest":{"id":"PR_node"}}}}""";
    private const string MarkedJson = """{"data":{"markFileAsViewed":{"pullRequest":{"id":"PR_node"}}}}""";

    [Fact]
    public async Task A_read_is_sent_again_once_when_the_connection_drops()
    {
        var (provider, http) = Create();
        var failures = 1;
        http.Routes[GraphQL] = _ => failures-- > 0 ? throw Dropped() : FakeHttpHandler.Json(http.Bodies[^1]!.Contains("mutation") ? MarkedJson : IdJson);

        await provider.SetFileViewedAsync(7, "src/a.py", viewed: true);

        // The id lookup twice (dropped, then answered), then the mark.
        Assert.Equal(3, http.Requests.Count);
        Assert.Equal(http.Bodies[0], http.Bodies[1]);
    }

    [Fact]
    public async Task A_write_is_not_sent_again_and_the_error_says_why()
    {
        var (provider, http) = Create();
        http.Routes[GraphQL] = _ => http.Bodies[^1]!.Contains("mutation") ? throw Dropped() : FakeHttpHandler.Json(IdJson);

        var ex = await Assert.ThrowsAsync<HostException>(() => provider.SetFileViewedAsync(7, "src/a.py", viewed: true));

        // It may have reached GitHub before the connection failed, so it's sent once.
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal(HostErrorKind.Network, ex.Kind);
        Assert.Contains("An existing connection was forcibly closed by the remote host.", ex.Message);
        Assert.DoesNotContain("An error occurred while sending the request", ex.Message);
    }

    [Fact]
    public async Task A_read_that_fails_twice_is_an_error()
    {
        var (provider, http) = Create();
        http.Routes[GraphQL] = _ => throw Dropped();

        var ex = await Assert.ThrowsAsync<HostException>(() => provider.SetFileViewedAsync(7, "src/a.py", viewed: true));

        Assert.Equal(2, http.Requests.Count);
        Assert.StartsWith("Couldn't reach GitHub: An existing connection was forcibly closed", ex.Message);
    }

    [Fact]
    public async Task Marks_and_unmarks_a_file_as_viewed_by_pull_request_id()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => FakeHttpHandler.Json("""{"data":{"repository":{"pullRequest":{"id":"PR_node"}},"markFileAsViewed":{"pullRequest":{"id":"PR_node"}}}}"""));

        await provider.SetFileViewedAsync(7, "src/a.py", viewed: true);
        await provider.SetFileViewedAsync(7, "src/a.py", viewed: false);

        // The id is looked up once, then cached.
        Assert.Equal(3, http.Requests.Count);
        Assert.Contains("pullRequest(number: $n) { id }", Body(http, 0).GetProperty("query").GetString());
        var mark = Body(http, 1);
        Assert.Contains("markFileAsViewed", mark.GetProperty("query").GetString());
        Assert.Equal(("PR_node", "src/a.py"), (mark.GetProperty("variables").GetProperty("id").GetString(), mark.GetProperty("variables").GetProperty("path").GetString()));
        Assert.Contains("unmarkFileAsViewed", Body(http, 2).GetProperty("query").GetString());
    }

    [Fact]
    public async Task Maps_pull_request_details()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => FakeHttpHandler.Json(DetailsJson));

        var pr = await provider.GetPullRequestAsync(7);

        Assert.Equal(7, Body(http).GetProperty("variables").GetProperty("n").GetInt32());
        Assert.Equal(("Adds widgets.", "base1", "head1"), (pr.Body, pr.BaseSha, pr.Summary.HeadSha));
        Assert.Equal(MergeState.Behind, pr.MergeState);
        Assert.Equal((3, 11, 9, 4), (pr.CommitCount, pr.Additions, pr.Deletions, pr.ChangedFiles));
        Assert.False(pr.ViewerIsAuthor);
        Assert.Equal(ReviewDecision.ReviewRequired, pr.Summary.ReviewDecision);
        Assert.Equal(ChecksState.Pending, pr.Summary.Checks);

        Assert.Equal(
        [
            new Reviewer("carol", "https://a/carol", ReviewState.Commented, IsRequested: true), // asked again after commenting
            new Reviewer("Core team", "https://a/team", ReviewState.None, IsRequested: true),
            new Reviewer("bob", "https://a/bob", ReviewState.Approved, IsRequested: false),
        ], pr.Reviewers);

        Assert.Equal(
        [
            new CheckItem("build", CheckStatus.Failure, true, "https://ci/build", "2 errors"),
            new CheckItem("lint", CheckStatus.Pending, false, null),
            new CheckItem("docs", CheckStatus.Skipped, false, null),
            new CheckItem("ci/legacy", CheckStatus.Success, true, "https://ci/legacy", "All good"),
        ], pr.Checks);

        var outdated = pr.Threads[0];
        Assert.True(outdated.IsOutdated);
        Assert.Equal(new CommentAnchor("src/a.py", DiffSide.Right, null, null, 6, "old1"), outdated.Anchor);
        Assert.Equal(["bob", "ghost"], outdated.Comments.Select(c => c.Author.Login));
        Assert.True(outdated.ViewerCanResolve);

        var left = pr.Threads[1];
        // startLine equal to line is a single-line comment; the anchor's commit is where the comment was made.
        Assert.Equal(new CommentAnchor("src/gone.py", DiffSide.Left, 2, null, 2, "first1"), left.Anchor);
        Assert.False(left.ViewerCanResolve);

        var range = pr.Threads[2];
        Assert.Equal(new CommentAnchor("src/b.py", DiffSide.Right, 5, 3, 5, "head1"), range.Anchor);
        Assert.True(range.IsResolved);
        Assert.True(range.ViewerCanResolve); // resolved: it's viewerCanUnresolve that counts

        Assert.Equal(["IC1", "R3", "R2", "IC2"], pr.Timeline.Select(t => t.Id));
        Assert.Equal(TimelineKind.Review, pr.Timeline[2].Kind);
        Assert.Equal(ReviewState.Approved, pr.Timeline[2].ReviewState);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 11, 0, 0, TimeSpan.Zero), pr.Timeline[2].CreatedAt); // submitted, not created
        Assert.Equal(ReviewState.Commented, pr.Timeline[1].ReviewState);
    }

    [Fact]
    public async Task Maps_comment_reactions_permissions_and_who_resolved()
    {
        var (provider, http) = Create();
        var json = DetailsJson
            .Replace("""
                "diffSide":"RIGHT","viewerCanReply":true,"viewerCanResolve":false,"viewerCanUnresolve":true,
                """, """
                "diffSide":"RIGHT","viewerCanReply":true,"viewerCanResolve":false,"viewerCanUnresolve":true,
                "resolvedBy":{"__typename":"User","login":"bob","avatarUrl":null,"name":"Bob B","databaseId":7},
                "hunk":{"nodes":[{"diffHunk":"@@ -1,2 +1,2 @@\n a\n+b"}]},
                """)
            .Replace("""
                {"id":"C4","body":"Range","createdAt":"2026-09-30T08:20:00Z","author":{"login":"bob","avatarUrl":null},
                """, """
                {"id":"C4","body":"Range","createdAt":"2026-09-30T08:20:00Z","author":{"__typename":"Bot","login":"rabbit","avatarUrl":null},
                 "url":"https://github.com/octo/widgets/pull/7#discussion_r1","authorAssociation":"MEMBER","lastEditedAt":"2026-09-30T09:00:00Z",
                 "isMinimized":true,"minimizedReason":"off-topic","viewerCanUpdate":true,"viewerCanDelete":false,"viewerCanMinimize":true,"viewerCanReact":true,
                 "reactionGroups":[
                   {"content":"HEART","viewerHasReacted":false,"reactors":{"totalCount":1,"nodes":[{"__typename":"User","login":"carol","name":null}]}},
                   {"content":"THUMBS_UP","viewerHasReacted":true,"reactors":{"totalCount":3,"nodes":[{"__typename":"User","login":"me","name":"Me Myself"},{"__typename":"Bot","login":"rabbit"}]}},
                   {"content":"EYES","viewerHasReacted":false,"reactors":{"totalCount":0,"nodes":[]}}],
                """);
        http.Route(GraphQL, () => FakeHttpHandler.Json(json));

        var pr = await provider.GetPullRequestAsync(7);

        var thread = pr.Threads[2];
        Assert.Equal("bob", thread.ResolvedBy?.Login);
        Assert.Equal(7, thread.ResolvedBy?.Id);
        Assert.Equal("@@ -1,2 +1,2 @@\n a\n+b", thread.DiffHunk);
        Assert.Null(pr.Threads[0].ResolvedBy); // not resolved

        var c = thread.Comments[0];
        Assert.True(c.Author.IsBot);
        Assert.Equal("https://github.com/octo/widgets/pull/7#discussion_r1", c.Url);
        Assert.Equal(AuthorRole.Member, c.Role);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero), c.EditedAt);
        Assert.Equal((true, HideReason.OffTopic), (c.IsHidden, c.HiddenReason));
        Assert.Equal((true, false, true, true), (c.ViewerCanEdit, c.ViewerCanDelete, c.ViewerCanHide, c.ViewerCanReact));
        // Unused kinds are left out; GitHub's order (thumbs up first) whatever order they came in.
        Assert.Equal([Reaction.ThumbsUp, Reaction.Heart], c.Reactions.Select(r => r.Reaction));
        Assert.Equal((3, true), (c.Reactions[0].Count, c.Reactions[0].ViewerReacted));
        Assert.Equal(["Me Myself", "rabbit"], c.Reactions[0].Reactors.Select(r => r.DisplayName));

        Assert.Null(pr.Threads[0].Comments[0].Url);
        Assert.Empty(pr.Threads[0].Comments[0].Reactions);
    }

    [Theory]
    [InlineData("CLEAN", "MERGEABLE", MergeState.Clean)]
    [InlineData("BLOCKED", "MERGEABLE", MergeState.Blocked)]
    [InlineData("BLOCKED", "CONFLICTING", MergeState.Conflicting)]
    [InlineData("DIRTY", "UNKNOWN", MergeState.Conflicting)]
    [InlineData("UNSTABLE", "MERGEABLE", MergeState.Unstable)]
    [InlineData("DRAFT", "MERGEABLE", MergeState.Draft)]
    [InlineData("UNKNOWN", "UNKNOWN", MergeState.Unknown)]
    public async Task Maps_merge_state(string status, string mergeable, MergeState expected)
    {
        var (provider, http) = Create();
        var json = DetailsJson.Replace("\"mergeStateStatus\":\"BEHIND\",\"mergeable\":\"MERGEABLE\"", $"\"mergeStateStatus\":\"{status}\",\"mergeable\":\"{mergeable}\"");
        http.Route(GraphQL, () => FakeHttpHandler.Json(json));
        Assert.Equal(expected, (await provider.GetPullRequestAsync(7)).MergeState);
    }

    // ---- Writes ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Adds_a_conversation_comment()
    {
        var (provider, http) = Create();
        http.Route(Rest, () => FakeHttpHandler.Json("{}", HttpStatusCode.Created));

        await provider.AddCommentAsync(7, "Looks good");

        Assert.Equal(HttpMethod.Post, http.Requests[0].Method);
        Assert.Equal($"{Rest}/issues/7/comments", http.Requests[0].RequestUri!.ToString());
        Assert.Equal("""{"body":"Looks good"}""", http.Bodies[0]);
    }

    [Fact]
    public async Task Adds_single_and_multi_line_comments()
    {
        var (provider, http) = Create();
        http.Route(Rest, () => FakeHttpHandler.Json("{}", HttpStatusCode.Created));

        await provider.AddLineCommentAsync(7, new CommentAnchor("src/gone.py", DiffSide.Left, 2), "Why?", "head1");
        await provider.AddLineCommentAsync(7, new CommentAnchor("src/b.py", DiffSide.Right, 5, StartLine: 3), "Range", "head1");

        Assert.Equal($"{Rest}/pulls/7/comments", http.Requests[0].RequestUri!.ToString());
        var single = Body(http, 0);
        Assert.Equal("head1", single.GetProperty("commit_id").GetString());
        Assert.Equal("src/gone.py", single.GetProperty("path").GetString());
        Assert.Equal(2, single.GetProperty("line").GetInt32());
        Assert.Equal("LEFT", single.GetProperty("side").GetString());
        Assert.False(single.TryGetProperty("start_line", out _));

        var range = Body(http, 1);
        Assert.Equal("RIGHT", range.GetProperty("side").GetString());
        Assert.Equal(5, range.GetProperty("line").GetInt32());
        Assert.Equal(3, range.GetProperty("start_line").GetInt32());
        Assert.Equal("RIGHT", range.GetProperty("start_side").GetString());
    }

    [Theory]
    [InlineData(ReviewVerdict.Comment, "COMMENT")]
    [InlineData(ReviewVerdict.Approve, "APPROVE")]
    [InlineData(ReviewVerdict.RequestChanges, "REQUEST_CHANGES")]
    public async Task Submits_a_review_with_its_comments(ReviewVerdict verdict, string expectedEvent)
    {
        var (provider, http) = Create();
        http.Route(Rest, () => FakeHttpHandler.Json("{}"));

        await provider.SubmitReviewAsync(7, verdict, "Summary",
            [
                new DraftComment(new CommentAnchor("src/a.py", DiffSide.Right, 10), "One"),
                new DraftComment(new CommentAnchor("src/gone.py", DiffSide.Left, 4, StartLine: 2), "Two"),
            ], "head1");

        Assert.Equal($"{Rest}/pulls/7/reviews", http.Requests[0].RequestUri!.ToString());
        var body = Body(http);
        Assert.Equal(expectedEvent, body.GetProperty("event").GetString());
        Assert.Equal("head1", body.GetProperty("commit_id").GetString());
        Assert.Equal("Summary", body.GetProperty("body").GetString());
        var comments = body.GetProperty("comments");
        Assert.Equal(2, comments.GetArrayLength());
        Assert.Equal("src/a.py", comments[0].GetProperty("path").GetString());
        Assert.Equal(10, comments[0].GetProperty("line").GetInt32());
        Assert.Equal("RIGHT", comments[0].GetProperty("side").GetString());
        Assert.Equal("One", comments[0].GetProperty("body").GetString());
        Assert.False(comments[0].TryGetProperty("commit_id", out _));
        Assert.Equal("LEFT", comments[1].GetProperty("side").GetString());
        Assert.Equal(2, comments[1].GetProperty("start_line").GetInt32());
        Assert.Equal("LEFT", comments[1].GetProperty("start_side").GetString());
    }

    [Fact]
    public async Task Review_without_a_body_or_comments_sends_neither()
    {
        var (provider, http) = Create();
        http.Route(Rest, () => FakeHttpHandler.Json("{}"));
        await provider.SubmitReviewAsync(7, ReviewVerdict.Approve, "  ", [], "head1");
        var body = Body(http);
        Assert.False(body.TryGetProperty("body", out _));
        Assert.False(body.TryGetProperty("comments", out _));
    }

    // ---- A review in progress (started on github.com or in another tool) -----------------------------------------

    // GitHub's answers, word for word, when the user already has a pending review.
    private static HttpResponseMessage OnePendingComment() => FakeHttpHandler.Json(
        """{"message":"Validation Failed","errors":[{"resource":"PullRequestReview","code":"custom","field":"user_id","message":"user_id can only have one pending review per pull request"}]}""",
        HttpStatusCode.UnprocessableEntity);

    private static HttpResponseMessage OnePendingReview() => FakeHttpHandler.Json(
        """{"message":"Unprocessable Entity","errors":["User can only have one pending review per pull request"]}""", HttpStatusCode.UnprocessableEntity);

    private static void RoutePendingReview(FakeHttpHandler http, string? reviewId) =>
        http.Routes[GraphQL] = _ => FakeHttpHandler.Json(http.Bodies[^1]!.Contains("states: PENDING")
            ? """{"data":{"repository":{"pullRequest":{"reviews":{"nodes":[""" + (reviewId is null ? "" : $"{{\"id\":\"{reviewId}\"}}") + "]}}}}}"
            : """{"data":{}}""");

    [Fact]
    public async Task Shows_the_review_in_progress_and_its_comments_as_pending()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => FakeHttpHandler.Json(DetailsJson
            .Replace("""
                "author":{"login":"me","avatarUrl":null}}]}
                """, """
                "author":{"login":"me","avatarUrl":null},"comments":{"totalCount":2}}]}
                """)
            .Replace("\"body\":\"Range\",", "\"body\":\"Range\",\"state\":\"PENDING\",")));

        var pr = await provider.GetPullRequestAsync(7);

        Assert.Equal(2, pr.PendingReviewComments);
        Assert.True(pr.Threads.Single(t => t.Id == "T3").Comments[0].IsPending);
        Assert.False(pr.Threads.Single(t => t.Id == "T2").Comments[0].IsPending);

        // Without one, there's none.
        http.Route(GraphQL, () => FakeHttpHandler.Json(DetailsJson.Replace("\"state\":\"PENDING\"", "\"state\":\"COMMENTED\"")));
        Assert.Null((await provider.GetPullRequestAsync(7)).PendingReviewComments);
    }

    [Fact]
    public async Task A_line_comment_joins_the_review_in_progress()
    {
        var (provider, http) = Create();
        http.Route(Rest, OnePendingComment);
        RoutePendingReview(http, "PRR_1");

        var posted = await provider.AddLineCommentAsync(7, new CommentAnchor("src/b.py", DiffSide.Right, 5, StartLine: 3), "Range", "head1");

        Assert.False(posted);
        Assert.Equal(3, http.Requests.Count);
        var (add, vars) = Call(http, 2);
        Assert.Contains("addPullRequestReviewThread", add);
        Assert.Equal(("PRR_1", "src/b.py", "Range"), (vars.GetProperty("review").GetString(), vars.GetProperty("path").GetString(), vars.GetProperty("body").GetString()));
        Assert.Equal((5, "RIGHT", 3, "RIGHT"), (vars.GetProperty("line").GetInt32(), vars.GetProperty("side").GetString(),
            vars.GetProperty("startLine").GetInt32(), vars.GetProperty("startSide").GetString()));
    }

    [Fact]
    public async Task A_review_submits_the_review_in_progress_with_its_comments()
    {
        var (provider, http) = Create();
        http.Route(Rest, OnePendingReview);
        RoutePendingReview(http, "PRR_1");

        await provider.SubmitReviewAsync(7, ReviewVerdict.Approve, "Looks good",
            [new DraftComment(new CommentAnchor("src/a.py", DiffSide.Right, 10), "One"), new DraftComment(new CommentAnchor("src/a.py", DiffSide.Left, 4), "Two")], "head1");

        // The REST review, the lookup, both comments into the pending review, then it's submitted.
        Assert.Equal(5, http.Requests.Count);
        Assert.Equal("One", Call(http, 2).Variables.GetProperty("body").GetString());
        Assert.Equal("LEFT", Call(http, 3).Variables.GetProperty("side").GetString());
        Assert.False(Call(http, 3).Variables.TryGetProperty("startLine", out _));
        var (submit, vars) = Call(http, 4);
        Assert.Contains("submitPullRequestReview", submit);
        Assert.Equal(("PRR_1", "APPROVE", "Looks good"), (vars.GetProperty("review").GetString(), vars.GetProperty("event").GetString(), vars.GetProperty("body").GetString()));
    }

    [Fact]
    public async Task Without_a_review_in_progress_GitHubs_refusal_stands()
    {
        var (provider, http) = Create();
        http.Route(Rest, OnePendingReview);
        RoutePendingReview(http, null);

        var ex = await Assert.ThrowsAsync<HostException>(() => provider.SubmitReviewAsync(7, ReviewVerdict.Comment, "Hm", [], "head1"));

        Assert.Contains("one pending review", ex.Message);
        Assert.Equal(2, http.Requests.Count);
    }

    private static ReviewThread Thread(string id) => new(id, new CommentAnchor("a", DiffSide.Right, 1), false, false, [], true, true);

    [Fact]
    public async Task Replies_and_resolves_by_thread_id()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => FakeHttpHandler.Json("""{"data":{}}"""));

        await provider.ReplyAsync(7, Thread("PRRT_1"), "Done");
        await provider.SetResolvedAsync(Thread("PRRT_1"), true);
        await provider.SetResolvedAsync(Thread("PRRT_1"), false);

        var reply = Body(http, 0);
        Assert.Contains("addPullRequestReviewThreadReply", reply.GetProperty("query").GetString());
        Assert.Equal("PRRT_1", reply.GetProperty("variables").GetProperty("id").GetString());
        Assert.Equal("Done", reply.GetProperty("variables").GetProperty("body").GetString());
        Assert.Contains("resolveReviewThread", Body(http, 1).GetProperty("query").GetString());
        Assert.DoesNotContain("unresolve", Body(http, 1).GetProperty("query").GetString());
        Assert.Contains("unresolveReviewThread", Body(http, 2).GetProperty("query").GetString());
        Assert.Equal("PRRT_1", Body(http, 2).GetProperty("variables").GetProperty("id").GetString());
    }

    private static readonly PrComment Comment1 = new("PRRC_1", new PrUser("bob", null), "Hi", DateTimeOffset.UnixEpoch);

    private static (string Query, JsonElement Variables) Call(FakeHttpHandler http, int index)
    {
        var body = Body(http, index);
        return (body.GetProperty("query").GetString()!, body.GetProperty("variables"));
    }

    [Fact]
    public async Task Reacts_edits_deletes_and_hides_comments_by_id()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => FakeHttpHandler.Json("""{"data":{}}"""));

        await provider.ReactAsync(Comment1, Reaction.Hooray, add: true);
        await provider.ReactAsync(Comment1, Reaction.ThumbsDown, add: false);
        await provider.EditCommentAsync(Comment1, "Better");
        await provider.DeleteCommentAsync(Comment1);
        await provider.SetHiddenAsync(Comment1, HideReason.OffTopic);
        await provider.SetHiddenAsync(Comment1, null);

        var (add, addVars) = Call(http, 0);
        Assert.Contains("addReaction", add);
        Assert.Equal(("PRRC_1", "HOORAY"), (addVars.GetProperty("id").GetString(), addVars.GetProperty("content").GetString()));
        var (remove, removeVars) = Call(http, 1);
        Assert.Contains("removeReaction", remove);
        Assert.Equal("THUMBS_DOWN", removeVars.GetProperty("content").GetString());
        var (edit, editVars) = Call(http, 2);
        Assert.Contains("updatePullRequestReviewComment", edit);
        Assert.Equal(("PRRC_1", "Better"), (editVars.GetProperty("id").GetString(), editVars.GetProperty("body").GetString()));
        Assert.Contains("deletePullRequestReviewComment", Call(http, 3).Query);
        var (hide, hideVars) = Call(http, 4);
        Assert.Contains("minimizeComment", hide);
        Assert.Equal("OFF_TOPIC", hideVars.GetProperty("reason").GetString());
        Assert.Contains("unminimizeComment", Call(http, 5).Query);
    }

    [Fact]
    public async Task Reads_edit_history_and_people_to_mention()
    {
        var (provider, http) = Create();
        var call = 0;
        http.Routes[GraphQL] = _ => FakeHttpHandler.Json(call++ == 0
            ? """
              {"data":{"node":{"userContentEdits":{"nodes":[
                {"editedAt":"2026-09-30T10:00:00Z","createdAt":"2026-09-30T10:00:01Z","editor":{"__typename":"User","login":"bob","avatarUrl":null,"name":"Bob B"}},
                {"editedAt":null,"createdAt":"2026-09-30T09:00:00Z","editor":null}]}}}}
              """
            : """{"data":{"repository":{"mentionableUsers":{"nodes":[{"login":"carol","name":"Carol C","avatarUrl":"https://a/carol","databaseId":9}]}}}}""");

        var edits = await provider.GetEditsAsync(Comment1);
        var people = await provider.SearchMentionsAsync("car");

        Assert.Equal(["bob", "ghost"], edits.Select(e => e.Editor.Login));
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero), edits[1].At); // no edit time: when it was written
        Assert.Equal("PRRC_1", Call(http, 0).Variables.GetProperty("id").GetString());
        Assert.Equal(("carol", "Carol C", 9L), (people[0].Login, people[0].Name, people[0].Id));
        Assert.Equal(("octo", "widgets", "car"), (Call(http, 1).Variables.GetProperty("owner").GetString(),
            Call(http, 1).Variables.GetProperty("repo").GetString(), Call(http, 1).Variables.GetProperty("q").GetString()));
    }

    [Fact]
    public async Task Reads_merge_options_with_the_viewers_last_method_first()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => FakeHttpHandler.Json("""
            {"data":{"repository":{"mergeCommitAllowed":false,"squashMergeAllowed":true,"rebaseMergeAllowed":true,
              "deleteBranchOnMerge":true,"viewerDefaultMergeMethod":"MERGE"}}}
            """));

        var options = await provider.GetMergeOptionsAsync();

        Assert.Equal([MergeMethod.Squash, MergeMethod.Rebase], options.Methods);
        Assert.Equal(MergeMethod.Squash, options.Default); // their last method isn't allowed any more
        Assert.True(options.DeletesBranchOnMerge);
    }

    [Fact]
    public async Task Merges_at_the_expected_head()
    {
        var (provider, http) = Create();
        http.Route(Rest, () => FakeHttpHandler.Json("""{"merged":true}"""));

        await provider.MergeAsync(7, MergeMethod.Squash, "head1", "Widgets (#7)");
        await provider.MergeAsync(7, MergeMethod.Rebase, "head1", "ignored for a rebase");

        Assert.Equal(HttpMethod.Put, http.Requests[0].Method);
        Assert.Equal($"{Rest}/pulls/7/merge", http.Requests[0].RequestUri!.ToString());
        Assert.Equal("""{"merge_method":"squash","sha":"head1","commit_title":"Widgets (#7)"}""", http.Bodies[0]);
        Assert.Equal("""{"merge_method":"rebase","sha":"head1"}""", http.Bodies[1]);
    }

    // ---- Errors ------------------------------------------------------------------------------------------------

    private static async Task<HostException> Fails(IPullRequestProvider provider) =>
        await Assert.ThrowsAsync<HostException>(() => provider.ListOpenAsync());

    [Fact]
    public async Task No_token_asks_to_sign_in_without_calling()
    {
        var (provider, http) = Create(token: null);
        var ex = await Fails(provider);
        Assert.Equal(HostErrorKind.NotAuthenticated, ex.Kind);
        Assert.Contains("gh auth login", ex.Message);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Rejected_token_is_not_authenticated()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => FakeHttpHandler.Json("""{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized));
        Assert.Equal(HostErrorKind.NotAuthenticated, (await Fails(provider)).Kind);
    }

    [Fact]
    public async Task Sso_header_needs_authorising()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () =>
        {
            var response = FakeHttpHandler.Json("""{"message":"Resource protected by organization SAML enforcement."}""", HttpStatusCode.Forbidden);
            response.Headers.Add("X-GitHub-SSO", "required; url=https://github.com/orgs/octo/sso?authorization_request=x");
            return response;
        });
        var ex = await Fails(provider);
        Assert.Equal(HostErrorKind.SsoRequired, ex.Kind);
        Assert.Contains("single sign-on", ex.Message);
    }

    [Fact]
    public async Task Sso_graphql_error_needs_authorising()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => FakeHttpHandler.Json("""
            {"data":{"repository":null},"errors":[{"type":"FORBIDDEN","message":"Resource protected by organization SAML enforcement. You must grant your Personal Access token access to this organization."}]}
            """));
        Assert.Equal(HostErrorKind.SsoRequired, (await Fails(provider)).Kind);
    }

    [Fact]
    public async Task Exhausted_rate_limit_fails_fast_until_reset()
    {
        var (provider, http) = Create();
        var reset = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds();
        http.Route(GraphQL, () =>
        {
            var response = FakeHttpHandler.Json("""{"message":"API rate limit exceeded for user ID 1."}""", HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", reset.ToString());
            response.Headers.Add("X-RateLimit-Resource", "graphql");
            return response;
        });

        Assert.Equal(HostErrorKind.RateLimited, (await Fails(provider)).Kind);
        Assert.Equal(HostErrorKind.RateLimited, (await Fails(provider)).Kind);
        Assert.Single(http.Requests);

        // REST has its own limit.
        http.Route(Rest, () => FakeHttpHandler.Json("{}"));
        await provider.AddCommentAsync(1, "x");
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task Secondary_rate_limit_is_rate_limited()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => FakeHttpHandler.Json("""{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}""", HttpStatusCode.Forbidden));
        Assert.Equal(HostErrorKind.RateLimited, (await Fails(provider)).Kind);
    }

    [Fact]
    public async Task GraphQL_errors_with_200_fail()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => FakeHttpHandler.Json("""{"data":null,"errors":[{"message":"Field 'nope' doesn't exist on type 'Query'"},{"message":"second"}]}"""));
        var ex = await Fails(provider);
        Assert.Equal(HostErrorKind.Other, ex.Kind);
        Assert.Contains("Field 'nope' doesn't exist", ex.Message);
    }

    [Fact]
    public async Task Missing_pull_request_is_not_found()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => FakeHttpHandler.Json("""
            {"data":{"viewer":{"login":"me"},"repository":{"pullRequest":null}},"errors":[{"type":"NOT_FOUND","path":["repository","pullRequest"],"message":"Could not resolve to a PullRequest with the number of 99."}]}
            """));
        var ex = await Assert.ThrowsAsync<HostException>(() => provider.GetPullRequestAsync(99));
        Assert.Equal(HostErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Rest_404_is_not_found()
    {
        var (provider, http) = Create();
        http.Route(Rest, () => FakeHttpHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<HostException>(() => provider.AddCommentAsync(7, "x"));
        Assert.Equal(HostErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Rest_validation_errors_are_shown()
    {
        var (provider, http) = Create();
        http.Route(Rest, () => FakeHttpHandler.Json("""{"message":"Validation Failed","errors":["line must be part of the diff"]}""", HttpStatusCode.UnprocessableEntity));
        var ex = await Assert.ThrowsAsync<HostException>(() => provider.AddLineCommentAsync(7, new CommentAnchor("a", DiffSide.Right, 99), "x", "h"));
        Assert.Equal(HostErrorKind.Other, ex.Kind);
        Assert.Contains("only takes them on changed lines", ex.Message);
    }

    [Fact]
    public async Task A_comment_GitHub_cant_place_says_where_comments_can_go()
    {
        var (provider, http) = Create();
        http.Route(Rest, () => FakeHttpHandler.Json("""{"message":"Validation Failed","errors":["could not be resolved"]}""", HttpStatusCode.UnprocessableEntity));
        var draft = new DraftComment(new CommentAnchor("a", DiffSide.Right, 24), "x");
        var ex = await Assert.ThrowsAsync<HostException>(() => provider.SubmitReviewAsync(7, ReviewVerdict.Comment, "", [draft], "h"));
        Assert.Contains("changed lines and the 3 lines either side", ex.Message);
    }

    [Fact]
    public async Task Unreachable_host_is_a_network_error()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => throw new HttpRequestException("No such host is known."));
        Assert.Equal(HostErrorKind.Network, (await Fails(provider)).Kind);
    }

    [Fact]
    public async Task Timeout_is_a_network_error_but_cancelling_is_not()
    {
        var (provider, http) = Create();
        http.Route(GraphQL, () => throw new TaskCanceledException("timed out"));
        Assert.Equal(HostErrorKind.Network, (await Fails(provider)).Kind);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListOpenAsync(cts.Token));
    }
}
