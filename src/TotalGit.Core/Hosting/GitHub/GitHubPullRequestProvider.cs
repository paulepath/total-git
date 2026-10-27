using System.Text.Json.Nodes;

namespace TotalGit.Core.Hosting.GitHub;

/// <summary>
/// Pull requests on a github.com repository. Reads use the GraphQL API (one call per list or pull request);
/// writes use REST, except those that address something by its GraphQL id: thread replies and resolving, and
/// reacting to, editing, deleting and hiding comments.
/// </summary>
public sealed class GitHubPullRequestProvider : IPullRequestProvider
{
    private readonly GitHubHttp _http;
    private readonly string _repoPath;
    private string? _viewer;

    internal GitHubPullRequestProvider(RemoteHostInfo host, GitHubHttp http)
    {
        Host = host;
        _http = http;
        _repoPath = $"repos/{Uri.EscapeDataString(host.Owner)}/{Uri.EscapeDataString(host.Repo)}";
    }

    public RemoteHostInfo Host { get; }

    public PrCapabilities Capabilities =>
        PrCapabilities.List | PrCapabilities.LineComments | PrCapabilities.Replies | PrCapabilities.ResolveThreads |
        PrCapabilities.Reviews | PrCapabilities.RequestChanges | PrCapabilities.Checks | PrCapabilities.Drafts |
        PrCapabilities.ViewedFiles | PrCapabilities.Reactions | PrCapabilities.EditComments | PrCapabilities.HideComments |
        PrCapabilities.Mentions | PrCapabilities.Merge;

    /// <summary>Pull request node ids by number, for the viewed-file mutations.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _nodeIds = new();

    /// <summary>How many pages of files to read at most (GitHub lists up to 3,000 files).</summary>
    private const int MaxFilePages = 30;

    public string HeadRefSpec(int number) => $"refs/pull/{number}/head";

    public async Task<string> GetViewerAsync(CancellationToken ct = default)
    {
        if (_viewer is not null) return _viewer;
        var data = await _http.GraphQLAsync(GitHubQueries.Viewer, [], ct);
        return _viewer = Viewer(data);
    }

    public async Task<IReadOnlyList<PullRequestSummary>> ListOpenAsync(CancellationToken ct = default)
    {
        var variables = RepoVariables();
        variables["requested"] = GitHubQueries.TeamRequested(Host.Owner, Host.Repo);
        var data = await _http.GraphQLAsync(GitHubQueries.ListOpen, variables, ct);
        var viewer = Viewer(data);
        var requested = data.Obj("requested")?.Obj("nodes") is { ValueKind: System.Text.Json.JsonValueKind.Array } found
            ? found.EnumerateArray().Select(n => n.Int("number")).OfType<int>().ToHashSet()
            : [];
        var nodes = data.Obj("repository")?.Obj("pullRequests")?.Obj("nodes");
        return nodes is { } array ? array.EnumerateArray().Select(pr => GitHubMapping.Summary(pr, viewer, requested)).ToList() : [];
    }

    public async Task<PullRequestDetails> GetPullRequestAsync(int number, CancellationToken ct = default)
    {
        var variables = RepoVariables();
        variables["n"] = number;
        var data = await _http.GraphQLAsync(GitHubQueries.Details, variables, ct);
        var pr = data.Obj("repository")?.Obj("pullRequest")
            ?? throw new HostException(HostErrorKind.NotFound, $"Pull request #{number} wasn't found on GitHub.");
        var details = GitHubMapping.Details(pr, Viewer(data)) with { Viewer = data.Obj("viewer") is { } v ? GitHubMapping.User(v) : null };
        if (details.NodeId is { } id) _nodeIds[number] = id;

        // Pull requests with more than 100 files: read the rest of the viewed marks.
        var after = GitHubMapping.NextPage(pr.Obj("files"));
        if (after is null) return details;
        var viewed = new Dictionary<string, FileViewState>(details.ViewedFiles);
        for (var page = 1; after is not null && page < MaxFilePages; page++)
        {
            var more = RepoVariables();
            more["n"] = number;
            more["after"] = after;
            var files = (await _http.GraphQLAsync(GitHubQueries.Files, more, ct)).Obj("repository")?.Obj("pullRequest")?.Obj("files");
            GitHubMapping.AddViewedFiles(viewed, files);
            after = GitHubMapping.NextPage(files);
        }
        return details with { ViewedFiles = viewed };
    }

    public async Task SetFileViewedAsync(int number, string path, bool viewed, CancellationToken ct = default)
    {
        if (!_nodeIds.TryGetValue(number, out var id))
        {
            var variables = RepoVariables();
            variables["n"] = number;
            var data = await _http.GraphQLAsync(GitHubQueries.PullRequestId, variables, ct);
            id = data.Obj("repository")?.Obj("pullRequest")?.Str("id")
                 ?? throw new HostException(HostErrorKind.NotFound, $"Pull request #{number} wasn't found on GitHub.");
            _nodeIds[number] = id;
        }
        await _http.GraphQLAsync(viewed ? GitHubQueries.MarkViewed : GitHubQueries.UnmarkViewed,
            new JsonObject { ["id"] = id, ["path"] = path }, ct);
    }

    public Task AddCommentAsync(int number, string body, CancellationToken ct = default) =>
        _http.RestAsync(HttpMethod.Post, $"{_repoPath}/issues/{number}/comments", new JsonObject { ["body"] = body }, ct);

    public async Task<bool> AddLineCommentAsync(int number, CommentAnchor anchor, string body, string headSha, CancellationToken ct = default)
    {
        var comment = LineComment(anchor, body);
        comment["commit_id"] = headSha;
        try
        {
            await ExplainUnplacedAsync(_http.RestAsync(HttpMethod.Post, $"{_repoPath}/pulls/{number}/comments", comment, ct));
            return true;
        }
        catch (HostException ex) when (IsReviewInProgress(ex))
        {
            var review = await PendingReviewIdAsync(number, ct);
            if (review is null) throw;
            await ExplainUnplacedAsync(AddToReviewAsync(review, anchor, body, ct));
            return false;
        }
    }

    public Task ReplyAsync(int number, ReviewThread thread, string body, CancellationToken ct = default) =>
        _http.GraphQLAsync(GitHubQueries.Reply, new JsonObject { ["id"] = thread.Id, ["body"] = body }, ct);

    public Task SetResolvedAsync(ReviewThread thread, bool resolved, CancellationToken ct = default) =>
        _http.GraphQLAsync(resolved ? GitHubQueries.Resolve : GitHubQueries.Unresolve, new JsonObject { ["id"] = thread.Id }, ct);

    public Task ReactAsync(PrComment comment, Reaction reaction, bool add, CancellationToken ct = default) =>
        _http.GraphQLAsync(add ? GitHubQueries.AddReaction : GitHubQueries.RemoveReaction,
            new JsonObject { ["id"] = comment.Id, ["content"] = GitHubMapping.ReactionContent(reaction) }, ct);

    public Task EditCommentAsync(PrComment comment, string body, CancellationToken ct = default) =>
        _http.GraphQLAsync(GitHubQueries.EditComment, new JsonObject { ["id"] = comment.Id, ["body"] = body }, ct);

    public Task DeleteCommentAsync(PrComment comment, CancellationToken ct = default) =>
        _http.GraphQLAsync(GitHubQueries.DeleteComment, new JsonObject { ["id"] = comment.Id }, ct);

    public Task SetHiddenAsync(PrComment comment, HideReason? reason, CancellationToken ct = default) => reason is { } r
        ? _http.GraphQLAsync(GitHubQueries.Hide, new JsonObject { ["id"] = comment.Id, ["reason"] = GitHubMapping.HideReasonClassifier(r) }, ct)
        : _http.GraphQLAsync(GitHubQueries.Unhide, new JsonObject { ["id"] = comment.Id }, ct);

    public async Task<IReadOnlyList<CommentEdit>> GetEditsAsync(PrComment comment, CancellationToken ct = default)
    {
        var data = await _http.GraphQLAsync(GitHubQueries.Edits, new JsonObject { ["id"] = comment.Id }, ct);
        return GitHubMapping.Edits(data.Obj("node"));
    }

    public async Task<MergeOptions> GetMergeOptionsAsync(CancellationToken ct = default)
    {
        var data = await _http.GraphQLAsync(GitHubQueries.MergeOptions, RepoVariables(), ct);
        var repo = data.Obj("repository") ?? throw new HostException(HostErrorKind.NotFound, $"{Host.Owner}/{Host.Repo} wasn't found on GitHub.");
        var methods = new List<MergeMethod>();
        if (repo.Bool("mergeCommitAllowed")) methods.Add(MergeMethod.Merge);
        if (repo.Bool("squashMergeAllowed")) methods.Add(MergeMethod.Squash);
        if (repo.Bool("rebaseMergeAllowed")) methods.Add(MergeMethod.Rebase);
        var preferred = repo.Str("viewerDefaultMergeMethod") switch
        {
            "SQUASH" => MergeMethod.Squash,
            "REBASE" => MergeMethod.Rebase,
            _ => MergeMethod.Merge,
        };
        var fallback = methods.Count > 0 ? methods[0] : MergeMethod.Merge;
        return new MergeOptions(methods, methods.Contains(preferred) ? preferred : fallback, repo.Bool("deleteBranchOnMerge"));
    }

    public Task MergeAsync(int number, MergeMethod method, string expectedHeadSha, string? title, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["merge_method"] = method switch { MergeMethod.Squash => "squash", MergeMethod.Rebase => "rebase", _ => "merge" },
            ["sha"] = expectedHeadSha,
        };
        if (!string.IsNullOrWhiteSpace(title) && method != MergeMethod.Rebase) body["commit_title"] = title.Trim();
        return _http.RestAsync(HttpMethod.Put, $"{_repoPath}/pulls/{number}/merge", body, ct);
    }

    public async Task<IReadOnlyList<PrUser>> SearchMentionsAsync(string query, CancellationToken ct = default)
    {
        var variables = RepoVariables();
        variables["q"] = query;
        var data = await _http.GraphQLAsync(GitHubQueries.Mentionable, variables, ct);
        return GitHubMapping.Users(data.Obj("repository")?.Obj("mentionableUsers"));
    }

    public Task SubmitReviewAsync(int number, ReviewVerdict verdict, string body, IReadOnlyList<DraftComment> comments, string headSha, CancellationToken ct = default)
    {
        var review = new JsonObject
        {
            ["commit_id"] = headSha,
            ["event"] = verdict switch
            {
                ReviewVerdict.Approve => "APPROVE",
                ReviewVerdict.RequestChanges => "REQUEST_CHANGES",
                _ => "COMMENT",
            },
        };
        // GitHub rejects an empty body on a comment review but accepts it missing when there are line comments.
        if (!string.IsNullOrWhiteSpace(body)) review["body"] = body;
        if (comments.Count > 0) review["comments"] = new JsonArray([.. comments.Select(c => LineComment(c.Anchor, c.Body))]);
        return ExplainUnplacedAsync(PostReviewAsync());

        async Task PostReviewAsync()
        {
            try
            {
                await _http.RestAsync(HttpMethod.Post, $"{_repoPath}/pulls/{number}/reviews", review, ct);
            }
            // A review is already in progress (GitHub allows one per person): this one's comments join it, then it's sent.
            catch (HostException ex) when (IsReviewInProgress(ex))
            {
                var pending = await PendingReviewIdAsync(number, ct);
                if (pending is null) throw;
                foreach (var c in comments) await AddToReviewAsync(pending, c.Anchor, c.Body, ct);
                var submit = new JsonObject { ["review"] = pending, ["event"] = review["event"]!.GetValue<string>() };
                if (!string.IsNullOrWhiteSpace(body)) submit["body"] = body;
                await _http.GraphQLAsync(GitHubQueries.SubmitPendingReview, submit, ct);
            }
        }
    }

    /// <summary>
    /// GitHub's 422 for a lone comment or a new review while the user has one pending (started on github.com, in VS
    /// Code, …): "user_id can only have one pending review per pull request".
    /// </summary>
    private static bool IsReviewInProgress(HostException ex) => ex.Message.Contains("one pending review", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> PendingReviewIdAsync(int number, CancellationToken ct)
    {
        var variables = RepoVariables();
        variables["n"] = number;
        var data = await _http.GraphQLAsync(GitHubQueries.PendingReview, variables, ct);
        var nodes = data.Obj("repository")?.Obj("pullRequest")?.Obj("reviews")?.Obj("nodes");
        return nodes is { ValueKind: System.Text.Json.JsonValueKind.Array } array ? array.EnumerateArray().Select(r => r.Str("id")).FirstOrDefault(id => id is not null) : null;
    }

    /// <summary>Adds a line comment to a pending review; it's posted when the review is submitted.</summary>
    private Task AddToReviewAsync(string review, CommentAnchor anchor, string body, CancellationToken ct)
    {
        var line = anchor.Line ?? throw new ArgumentException("A new line comment needs a line.", nameof(anchor));
        var side = anchor.Side == DiffSide.Left ? "LEFT" : "RIGHT";
        var variables = new JsonObject { ["review"] = review, ["path"] = anchor.Path, ["body"] = body, ["line"] = line, ["side"] = side };
        if (anchor.StartLine is { } start && start != line)
        {
            variables["startLine"] = start;
            variables["startSide"] = side;
        }
        return _http.GraphQLAsync(GitHubQueries.AddReviewThread, variables, ct);
    }

    /// <summary>
    /// GitHub's "Validation Failed (could not be resolved)" (or "… must be part of the diff") means a line comment is
    /// outside its diff of the file.
    /// </summary>
    private static async Task ExplainUnplacedAsync(Task call)
    {
        try
        {
            await call;
        }
        catch (HostException ex) when (ex.Message.Contains("could not be resolved", StringComparison.OrdinalIgnoreCase)
                                       || ex.Message.Contains("must be part of the diff", StringComparison.OrdinalIgnoreCase))
        {
            throw new HostException(ex.Kind,
                $"GitHub couldn't place a line comment: it only takes them on changed lines and the {CommentableLines.HostContext} lines " +
                "either side. Move the comment nearer a change, or make it a general comment.", ex);
        }
    }

    /// <summary>A line comment's body and position, as both the comments and reviews endpoints take it.</summary>
    private static JsonObject LineComment(CommentAnchor anchor, string body)
    {
        var line = anchor.Line ?? throw new ArgumentException("A new line comment needs a line.", nameof(anchor));
        var side = anchor.Side == DiffSide.Left ? "LEFT" : "RIGHT";
        var comment = new JsonObject { ["body"] = body, ["path"] = anchor.Path, ["line"] = line, ["side"] = side };
        if (anchor.StartLine is { } start && start != line)
        {
            comment["start_line"] = start;
            comment["start_side"] = side;
        }
        return comment;
    }

    private JsonObject RepoVariables() => new() { ["owner"] = Host.Owner, ["repo"] = Host.Repo };

    private string Viewer(System.Text.Json.JsonElement data) =>
        _viewer = data.Obj("viewer")?.Str("login") ?? _viewer ?? "";
}
