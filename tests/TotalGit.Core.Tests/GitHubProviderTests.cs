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
        var factory = new GitHubPullRequestProviderFactory(new Token(token), new HttpClient(http));
        return (factory.TryCreate(Repo)!, http);
    }

    private static JsonElement Body(FakeHttpHandler http, int index = 0) => JsonDocument.Parse(http.Bodies[index]!).RootElement;

    [Fact]
    public void Factory_only_serves_github_com()
    {
        using var factory = new GitHubPullRequestProviderFactory(new Token("t"), new HttpClient(new FakeHttpHandler()));
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
           "reviewDecision":"CHANGES_REQUESTED","author":{"login":"alice","avatarUrl":"https://a/alice"},
           "reviewRequests":{"nodes":[{"requestedReviewer":null},{"requestedReviewer":{"__typename":"Team","name":"core","avatarUrl":null}},
                                      {"requestedReviewer":{"__typename":"User","login":"me","avatarUrl":"https://a/me"}}]},
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
           "latestReviews":{"nodes":[{"state":"APPROVED","author":{"login":"carol","avatarUrl":"https://a/carol"}}]},
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
        Assert.Equal(new PrUser("alice", "https://a/alice"), draft.Author);
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

        Assert.Equal(ReviewDecision.Approved, list[2].ReviewDecision);
        Assert.Equal(ChecksState.Pending, list[2].Checks);

        // Who it's for: requested users and teams, then people who reviewed without being asked.
        Assert.Equal(["core", "me"], draft.Reviewers.Select(r => r.Name));
        Assert.All(draft.Reviewers, r => Assert.True(r.IsRequested));
        Assert.Equal([new Reviewer("carol", "https://a/carol", ReviewState.Approved, IsRequested: false)], list[2].Reviewers);

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
        Assert.Contains("line must be part of the diff", ex.Message);
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
