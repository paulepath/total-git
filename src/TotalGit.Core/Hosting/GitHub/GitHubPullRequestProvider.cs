using System.Text.Json.Nodes;

namespace TotalGit.Core.Hosting.GitHub;

/// <summary>
/// Pull requests on a github.com repository. Reads use the GraphQL API (one call per list or pull request);
/// writes use REST, except thread replies and resolving, which only GraphQL can address by thread id.
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
        PrCapabilities.ViewedFiles;

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
        var data = await _http.GraphQLAsync(GitHubQueries.ListOpen, RepoVariables(), ct);
        var viewer = Viewer(data);
        var nodes = data.Obj("repository")?.Obj("pullRequests")?.Obj("nodes");
        return nodes is { } array ? array.EnumerateArray().Select(pr => GitHubMapping.Summary(pr, viewer)).ToList() : [];
    }

    public async Task<PullRequestDetails> GetPullRequestAsync(int number, CancellationToken ct = default)
    {
        var variables = RepoVariables();
        variables["n"] = number;
        var data = await _http.GraphQLAsync(GitHubQueries.Details, variables, ct);
        var pr = data.Obj("repository")?.Obj("pullRequest")
            ?? throw new HostException(HostErrorKind.NotFound, $"Pull request #{number} wasn't found on GitHub.");
        var details = GitHubMapping.Details(pr, Viewer(data));
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

    public Task AddLineCommentAsync(int number, CommentAnchor anchor, string body, string headSha, CancellationToken ct = default)
    {
        var comment = LineComment(anchor, body);
        comment["commit_id"] = headSha;
        return _http.RestAsync(HttpMethod.Post, $"{_repoPath}/pulls/{number}/comments", comment, ct);
    }

    public Task ReplyAsync(int number, ReviewThread thread, string body, CancellationToken ct = default) =>
        _http.GraphQLAsync(GitHubQueries.Reply, new JsonObject { ["id"] = thread.Id, ["body"] = body }, ct);

    public Task SetResolvedAsync(ReviewThread thread, bool resolved, CancellationToken ct = default) =>
        _http.GraphQLAsync(resolved ? GitHubQueries.Resolve : GitHubQueries.Unresolve, new JsonObject { ["id"] = thread.Id }, ct);

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
        return _http.RestAsync(HttpMethod.Post, $"{_repoPath}/pulls/{number}/reviews", review, ct);
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
