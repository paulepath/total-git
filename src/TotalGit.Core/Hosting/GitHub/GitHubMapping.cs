using System.Text.Json;

namespace TotalGit.Core.Hosting.GitHub;

/// <summary>Maps GitHub GraphQL responses (see <see cref="GitHubQueries"/>) to the provider-neutral records.</summary>
internal static class GitHubMapping
{
    public static PullRequestSummary Summary(JsonElement pr, string viewer) => new(
        pr.GetProperty("number").GetInt32(),
        pr.Str("title") ?? "",
        User(pr.Obj("author")),
        pr.Bool("isDraft"),
        pr.Str("state") switch { "MERGED" => PullRequestState.Merged, "CLOSED" => PullRequestState.Closed, _ => PullRequestState.Open },
        pr.Str("baseRefName") ?? "",
        pr.Str("headRefName") ?? "",
        pr.Str("headRefOid") ?? "",
        pr.Bool("isCrossRepository"),
        pr.GetProperty("updatedAt").GetDateTimeOffset(),
        ChecksState(Rollup(pr)?.Str("state")),
        pr.Str("reviewDecision") switch
        {
            "APPROVED" => ReviewDecision.Approved,
            "CHANGES_REQUESTED" => ReviewDecision.ChangesRequested,
            "REVIEW_REQUIRED" => ReviewDecision.ReviewRequired,
            _ => ReviewDecision.None,
        },
        // Only direct requests count; a request to one of the viewer's teams isn't detected.
        RequestedReviewers(pr).Any(r => r.Login is { } login && string.Equals(login, viewer, StringComparison.OrdinalIgnoreCase)),
        pr.Str("url") ?? "")
    {
        Reviewers = Reviewers(pr),
        IsViewerAuthor = pr.Obj("author")?.Str("login") is { } author && string.Equals(author, viewer, StringComparison.OrdinalIgnoreCase),
        CreatedAt = pr.Obj("createdAt") is { ValueKind: JsonValueKind.String } created ? created.GetDateTimeOffset() : null,
        Additions = pr.Int("additions"),
        Deletions = pr.Int("deletions"),
        ChangedFiles = pr.Int("changedFiles"),
        MergeState = MergeStateOf(pr),
        CheckRuns = Checks(pr),
    };

    private static MergeState MergeStateOf(JsonElement pr) =>
        pr.Str("mergeable") == "CONFLICTING" ? MergeState.Conflicting : pr.Str("mergeStateStatus") switch
        {
            "CLEAN" => MergeState.Clean,
            "BLOCKED" => MergeState.Blocked,
            "BEHIND" => MergeState.Behind,
            "DIRTY" => MergeState.Conflicting,
            "UNSTABLE" => MergeState.Unstable,
            "DRAFT" => MergeState.Draft,
            _ => MergeState.Unknown,
        };

    public static PullRequestDetails Details(JsonElement pr, string viewer)
    {
        var mergeState = MergeStateOf(pr);
        return new(
            Summary(pr, viewer),
            pr.Str("body") ?? "",
            pr.Str("baseRefOid") ?? "",
            mergeState,
            Reviewers(pr),
            Checks(pr),
            Nodes(pr.Obj("reviewThreads")).Select(Thread).ToList(),
            Timeline(pr),
            pr.Obj("commits")?.Int("totalCount") ?? 0,
            pr.Int("additions") ?? 0,
            pr.Int("deletions") ?? 0,
            pr.Int("changedFiles") ?? 0,
            pr.Bool("viewerDidAuthor"))
        {
            NodeId = pr.Str("id"),
            ViewedFiles = ViewedFiles(pr.Obj("files")),
            LastViewerReviewSha = LastReview(pr, viewer)?.Obj("commit")?.Str("oid"),
            LastViewerReviewAt = LastReview(pr, viewer) is { } r ? (r.Obj("submittedAt") ?? r.GetProperty("createdAt")).GetDateTimeOffset() : null,
        };
    }

    /// <summary>The signed-in user's newest submitted (not pending) review.</summary>
    private static JsonElement? LastReview(JsonElement pr, string viewer) =>
        Nodes(pr.Obj("reviews"))
            .Where(r => r.Str("state") != "PENDING" && string.Equals(r.Obj("author")?.Str("login"), viewer, StringComparison.OrdinalIgnoreCase))
            .Select(r => (JsonElement?)r)
            .LastOrDefault();

    /// <summary>The viewer's viewed marks from a page of a pull request's files.</summary>
    public static Dictionary<string, FileViewState> ViewedFiles(JsonElement? files)
    {
        var result = new Dictionary<string, FileViewState>();
        AddViewedFiles(result, files);
        return result;
    }

    public static void AddViewedFiles(Dictionary<string, FileViewState> into, JsonElement? files)
    {
        foreach (var f in Nodes(files))
        {
            if (f.Str("path") is not { } path) continue;
            into[path] = f.Str("viewerViewedState") switch
            {
                "VIEWED" => FileViewState.Viewed,
                "DISMISSED" => FileViewState.ChangedSinceViewed,
                _ => FileViewState.Unviewed,
            };
        }
    }

    /// <summary>The cursor for the next page of a connection, or null on the last page.</summary>
    public static string? NextPage(JsonElement? connection) =>
        connection?.Obj("pageInfo") is { } info && info.Bool("hasNextPage") ? info.Str("endCursor") : null;

    /// <summary>
    /// Requested reviewers first (carrying their earlier review's state when they've been asked again), then everyone
    /// else who has reviewed. GitHub leaves the author's own comment-reviews out of latestReviews.
    /// </summary>
    private static List<Reviewer> Reviewers(JsonElement pr)
    {
        var latest = Nodes(pr.Obj("latestReviews"))
            .Select(r => (User: User(r.Obj("author")), State: ReviewState(r.Str("state"))))
            .ToList();
        var reviewers = new List<Reviewer>();
        foreach (var (login, name, avatar, fullName) in RequestedReviewers(pr))
        {
            var earlier = login is null ? default : latest.FirstOrDefault(r => r.User.Login == login);
            reviewers.Add(new(name, avatar, earlier.User is null ? Hosting.ReviewState.None : earlier.State, IsRequested: true) { FullName = fullName });
        }
        foreach (var (user, state) in latest)
            if (!reviewers.Any(r => r.Name == user.Login))
                reviewers.Add(new(user.Login, user.AvatarUrl, state, IsRequested: false) { FullName = user.Name });
        return reviewers;
    }

    /// <summary>Requested users (with a login) and teams (without); requests the token can't see come back null and are skipped.</summary>
    private static IEnumerable<(string? Login, string Name, string? AvatarUrl, string? FullName)> RequestedReviewers(JsonElement pr) =>
        Nodes(pr.Obj("reviewRequests"))
            .Select(n => n.Obj("requestedReviewer"))
            .OfType<JsonElement>()
            .Select(r => r.Str("__typename") == "Team"
                ? ((string?)null, r.Str("name") ?? "", r.Str("avatarUrl"), (string?)null)
                : (r.Str("login"), r.Str("login") ?? "", r.Str("avatarUrl"), r.Str("name")))
            .Where(r => r.Item2.Length > 0);

    /// <summary>
    /// The commit's checks as GitHub's pull request page shows them. When a workflow ran more than once on the commit
    /// (pushed, then the pull request edited, or re-run), the rollup lists every run's check runs: only the newest
    /// run of each workflow counts, so a superseded run's failures (or its jobs cancelled by the newer run) don't.
    /// </summary>
    private static List<CheckItem> Checks(JsonElement pr)
    {
        var contexts = Nodes(Rollup(pr)?.Obj("contexts")).ToList();
        // The newest run of each workflow (runs started later have larger ids).
        var latestRun = contexts
            .Select(c => c.Obj("checkSuite")?.Obj("workflowRun"))
            .Where(r => r is not null && r.Value.Obj("workflow")?.Str("name") is not null)
            .GroupBy(r => r!.Value.Obj("workflow")!.Value.Str("name")!)
            .ToDictionary(g => g.Key, g => g.Max(r => r!.Value.Long("databaseId")));

        return contexts
            .Where(c =>
            {
                var run = c.Obj("checkSuite")?.Obj("workflowRun");
                var workflow = run?.Obj("workflow")?.Str("name");
                return workflow is null || run!.Value.Long("databaseId") == latestRun[workflow];
            })
            .Select(c => c.Str("__typename") == "StatusContext"
                ? new CheckItem(c.Str("context") ?? "", CheckStatus(c.Str("state")), c.Bool("isRequired"), c.Str("targetUrl"), c.Str("description"))
                // A check run's conclusion only exists once it has completed.
                : new CheckItem(c.Str("name") ?? "", CheckStatus(c.Str("status") == "COMPLETED" ? c.Str("conclusion") : c.Str("status")),
                    c.Bool("isRequired"), c.Str("detailsUrl"), c.Str("title")))
            .ToList();
    }

    private static ReviewThread Thread(JsonElement t)
    {
        var comments = Nodes(t.Obj("comments")).ToList();
        var first = comments.FirstOrDefault();
        var outdated = t.Bool("isOutdated");
        var line = outdated ? null : t.Int("line");
        // Single-line threads report startLine equal to line; only a different start makes a range.
        var startLine = t.Int("startLine") is { } s && s != line ? s : (int?)null;
        var sha = comments.Count == 0 ? null : first.Obj("originalCommit")?.Str("oid") ?? first.Obj("commit")?.Str("oid");
        var anchor = new CommentAnchor(t.Str("path") ?? "", t.Str("diffSide") == "LEFT" ? DiffSide.Left : DiffSide.Right,
            line, outdated ? null : startLine, t.Int("originalLine"), sha);
        var resolved = t.Bool("isResolved");
        return new(
            t.Str("id") ?? "",
            anchor,
            resolved,
            outdated,
            comments.Select(c => new PrComment(c.Str("id") ?? "", User(c.Obj("author")), c.Str("body") ?? "", c.GetProperty("createdAt").GetDateTimeOffset())).ToList(),
            t.Bool("viewerCanReply"),
            resolved ? t.Bool("viewerCanUnresolve") : t.Bool("viewerCanResolve"));
    }

    /// <summary>
    /// Conversation comments and submitted reviews, oldest first. Reviews without a body are left out unless they approve
    /// or request changes: every single line comment creates an empty "commented" review.
    /// </summary>
    private static List<TimelineItem> Timeline(JsonElement pr)
    {
        var comments = Nodes(pr.Obj("comments")).Select(c =>
            new TimelineItem(c.Str("id") ?? "", TimelineKind.Comment, User(c.Obj("author")), c.Str("body") ?? "", c.GetProperty("createdAt").GetDateTimeOffset()));
        var reviews = Nodes(pr.Obj("reviews"))
            .Where(r => r.Str("state") is "APPROVED" or "CHANGES_REQUESTED" || (r.Str("state") != "PENDING" && !string.IsNullOrWhiteSpace(r.Str("body"))))
            .Select(r => new TimelineItem(r.Str("id") ?? "", TimelineKind.Review, User(r.Obj("author")), r.Str("body") ?? "",
                (r.Obj("submittedAt") ?? r.GetProperty("createdAt")).GetDateTimeOffset(), ReviewState(r.Str("state"))));
        return comments.Concat(reviews).OrderBy(i => i.CreatedAt).ToList();
    }

    private static JsonElement? Rollup(JsonElement pr) =>
        Nodes(pr.Obj("commits")).FirstOrDefault() is { ValueKind: JsonValueKind.Object } node ? node.Obj("commit")?.Obj("statusCheckRollup") : null;

    private static ChecksState ChecksState(string? rollup) => rollup switch
    {
        "SUCCESS" => Hosting.ChecksState.Success,
        "FAILURE" or "ERROR" => Hosting.ChecksState.Failure,
        "PENDING" or "EXPECTED" => Hosting.ChecksState.Pending,
        _ => Hosting.ChecksState.None,
    };

    private static CheckStatus CheckStatus(string? value) => value switch
    {
        "SUCCESS" => Hosting.CheckStatus.Success,
        "FAILURE" or "ERROR" or "TIMED_OUT" or "STARTUP_FAILURE" or "ACTION_REQUIRED" => Hosting.CheckStatus.Failure,
        "NEUTRAL" => Hosting.CheckStatus.Neutral,
        "SKIPPED" => Hosting.CheckStatus.Skipped,
        "CANCELLED" or "STALE" => Hosting.CheckStatus.Cancelled,
        "QUEUED" or "IN_PROGRESS" or "PENDING" or "EXPECTED" or "WAITING" or "REQUESTED" => Hosting.CheckStatus.Pending,
        _ => Hosting.CheckStatus.Neutral, // completed without a conclusion
    };

    private static ReviewState ReviewState(string? value) => value switch
    {
        "APPROVED" => Hosting.ReviewState.Approved,
        "CHANGES_REQUESTED" => Hosting.ReviewState.ChangesRequested,
        "COMMENTED" => Hosting.ReviewState.Commented,
        "DISMISSED" => Hosting.ReviewState.Dismissed,
        "PENDING" => Hosting.ReviewState.Pending,
        _ => Hosting.ReviewState.None,
    };

    /// <summary>Deleted accounts come back as a null author; GitHub shows them as "ghost".</summary>
    private static PrUser User(JsonElement? user) =>
        user is { } u ? new(u.Str("login") ?? "ghost", u.Str("avatarUrl"), u.Str("name")) : new("ghost", null);

    private static IEnumerable<JsonElement> Nodes(JsonElement? connection) =>
        connection?.Obj("nodes") is { ValueKind: JsonValueKind.Array } nodes
            ? nodes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Object)
            : [];
}

/// <summary>Null-tolerant reads of GitHub JSON, where absent and null mean the same.</summary>
internal static class GitHubJson
{
    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static string? Str(this JsonElement e, string name) => e.Obj(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static int? Int(this JsonElement e, string name) => e.Obj(name) is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : null;

    public static long? Long(this JsonElement e, string name) => e.Obj(name) is { ValueKind: JsonValueKind.Number } v ? v.GetInt64() : null;

    public static bool Bool(this JsonElement e, string name) => e.Obj(name) is { ValueKind: JsonValueKind.True };
}
