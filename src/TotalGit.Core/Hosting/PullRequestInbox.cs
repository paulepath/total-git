namespace TotalGit.Core.Hosting;

/// <summary>
/// The signed-in user's open pull requests in every repository: those they're asked to review or assigned to, and
/// their own. Polled for notifications.
/// </summary>
public interface IPullRequestInbox
{
    Task<Inbox> GetAsync(CancellationToken ct = default);
}

/// <summary>Makes the inboxes to poll: one per account the user is signed in to.</summary>
public interface IPullRequestInboxSource
{
    Task<IReadOnlyList<IPullRequestInbox>> CreateInboxesAsync(CancellationToken ct = default);
}

/// <param name="Viewer">The signed-in user's login (what notifications are remembered under).</param>
public sealed record Inbox(string Viewer, IReadOnlyList<InboxPullRequest> PullRequests);

/// <summary>A pull request that involves the signed-in user, from any repository.</summary>
/// <param name="Repository">The repository as <c>owner/name</c>.</param>
public sealed record InboxPullRequest(string Repository, int Number, string Title, string Url, PrUser Author, bool IsDraft, string HeadSha)
{
    /// <summary>The user's review is requested (directly, not through a team).</summary>
    public bool ReviewRequested { get; init; }

    public bool Assigned { get; init; }

    /// <summary>The event of the user's latest review request (a new one each time they're asked again), if known.</summary>
    public string? ReviewRequestId { get; init; }

    /// <summary>The event of the user's latest assignment, if known.</summary>
    public string? AssignmentId { get; init; }

    public bool IsViewerAuthor { get; init; }

    /// <summary>The latest reviews (only fetched for the user's own pull requests), oldest first.</summary>
    public IReadOnlyList<InboxReview> Reviews { get; init; } = [];

    /// <summary>Recent conversation and line comments (only for the user's own pull requests).</summary>
    public IReadOnlyList<InboxComment> Comments { get; init; } = [];

    /// <summary>The head commit's checks, all together.</summary>
    public ChecksState Checks { get; init; }
}

/// <param name="HasBody">A "commented" review with something written in it (every line comment makes an empty one).</param>
public sealed record InboxReview(string Id, ReviewState State, PrUser Author, bool HasBody);

public sealed record InboxComment(string Id, PrUser Author);
