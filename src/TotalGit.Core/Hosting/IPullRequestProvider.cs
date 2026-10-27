namespace TotalGit.Core.Hosting;

/// <summary>What a provider supports; the app hides actions a provider can't do.</summary>
[Flags]
public enum PrCapabilities
{
    None = 0,
    List = 1 << 0,
    LineComments = 1 << 1,
    Replies = 1 << 2,
    ResolveThreads = 1 << 3,
    Reviews = 1 << 4,
    RequestChanges = 1 << 5,
    Checks = 1 << 6,
    Drafts = 1 << 7,

    /// <summary>Files can be marked as viewed (reviewed), and the host remembers it.</summary>
    ViewedFiles = 1 << 8,

    /// <summary>Comments take reactions (👍, 🎉…).</summary>
    Reactions = 1 << 9,

    /// <summary>Line comments can be edited and deleted, with their edit history readable.</summary>
    EditComments = 1 << 10,

    /// <summary>Comments can be hidden (minimized) with a reason, and shown again.</summary>
    HideComments = 1 << 11,

    /// <summary>People can be looked up for @mentions.</summary>
    Mentions = 1 << 12,

    /// <summary>Pull requests can be merged.</summary>
    Merge = 1 << 13,
}

/// <summary>
/// Pull requests on one repository of one hosting service. Implementations map the service's API to the
/// provider-neutral records in <see cref="PullRequestSummary"/> and friends. Diffs aren't part of this: the app
/// fetches <see cref="HeadRefSpec"/> and diffs locally with git.
/// </summary>
public interface IPullRequestProvider
{
    RemoteHostInfo Host { get; }
    PrCapabilities Capabilities { get; }

    /// <summary>The ref on the remote holding a pull request's head commit, e.g. <c>refs/pull/12/head</c>.</summary>
    string HeadRefSpec(int number);

    /// <summary>The signed-in user's login.</summary>
    Task<string> GetViewerAsync(CancellationToken ct = default);

    /// <summary>Open pull requests, most recently updated first.</summary>
    Task<IReadOnlyList<PullRequestSummary>> ListOpenAsync(CancellationToken ct = default);

    /// <summary>One pull request with its reviewers, checks, line comment threads and conversation.</summary>
    Task<PullRequestDetails> GetPullRequestAsync(int number, CancellationToken ct = default);

    /// <summary>A general comment on the pull request's conversation.</summary>
    Task AddCommentAsync(int number, string body, CancellationToken ct = default);

    /// <summary>
    /// A single line comment, posted at once (not part of a review). When the signed-in user has a review in progress
    /// (started on the host's website or in another tool), hosts won't post one on its own: it's added to that review
    /// instead, and false is returned.
    /// </summary>
    /// <param name="headSha">The pull request head the line numbers refer to.</param>
    /// <returns>True when it was posted; false when it waits in the review in progress.</returns>
    Task<bool> AddLineCommentAsync(int number, CommentAnchor anchor, string body, string headSha, CancellationToken ct = default);

    Task ReplyAsync(int number, ReviewThread thread, string body, CancellationToken ct = default);

    Task SetResolvedAsync(ReviewThread thread, bool resolved, CancellationToken ct = default);

    /// <summary>
    /// Submits a review with its line comments in one go. A review the signed-in user has in progress on the host is
    /// submitted with them, comments and all.
    /// </summary>
    /// <param name="headSha">The pull request head the review (and its comments' line numbers) refer to.</param>
    Task SubmitReviewAsync(int number, ReviewVerdict verdict, string body, IReadOnlyList<DraftComment> comments, string headSha, CancellationToken ct = default);

    /// <summary>Marks a file of the pull request as viewed by the signed-in user, or clears the mark.</summary>
    Task SetFileViewedAsync(int number, string path, bool viewed, CancellationToken ct = default);

    /// <summary>Adds the signed-in user's reaction to a comment, or takes it away.</summary>
    Task ReactAsync(PrComment comment, Reaction reaction, bool add, CancellationToken ct = default);

    /// <summary>Replaces a line comment's text.</summary>
    Task EditCommentAsync(PrComment comment, string body, CancellationToken ct = default);

    Task DeleteCommentAsync(PrComment comment, CancellationToken ct = default);

    /// <summary>Hides a comment for the given reason, or shows it again when <paramref name="reason"/> is null.</summary>
    Task SetHiddenAsync(PrComment comment, HideReason? reason, CancellationToken ct = default);

    /// <summary>A comment's versions, newest first (the last is when it was written).</summary>
    Task<IReadOnlyList<CommentEdit>> GetEditsAsync(PrComment comment, CancellationToken ct = default);

    /// <summary>The merge methods the repository allows, and whether it deletes merged branches itself.</summary>
    Task<MergeOptions> GetMergeOptionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Merges a pull request. Refused when its head is no longer <paramref name="expectedHeadSha"/> (someone pushed),
    /// or when the host won't merge it (checks, reviews, conflicts); the message says why.
    /// </summary>
    /// <param name="title">The commit's title, or null for the host's usual one.</param>
    Task MergeAsync(int number, MergeMethod method, string expectedHeadSha, string? title, CancellationToken ct = default);

    /// <summary>People who can be @mentioned in the repository whose login or name matches <paramref name="query"/>.</summary>
    Task<IReadOnlyList<PrUser>> SearchMentionsAsync(string query, CancellationToken ct = default);
}

/// <summary>Creates the provider for a repository's host; null when the host isn't supported.</summary>
public interface IPullRequestProviderFactory
{
    IPullRequestProvider? TryCreate(RemoteHostInfo host);
}

public enum HostErrorKind
{
    /// <summary>No token, or the token was rejected.</summary>
    NotAuthenticated,
    /// <summary>The organization uses single sign-on and the token isn't authorised for it.</summary>
    SsoRequired,
    RateLimited,
    NotFound,
    /// <summary>The host couldn't be reached.</summary>
    Network,
    Other,
}

/// <summary>A hosting service call failed; <see cref="Exception.Message"/> is fit to show the user.</summary>
public sealed class HostException(HostErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public HostErrorKind Kind { get; } = kind;
}
