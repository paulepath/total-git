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

    /// <summary>A single line comment, posted at once (not part of a review).</summary>
    /// <param name="headSha">The pull request head the line numbers refer to.</param>
    Task AddLineCommentAsync(int number, CommentAnchor anchor, string body, string headSha, CancellationToken ct = default);

    Task ReplyAsync(int number, ReviewThread thread, string body, CancellationToken ct = default);

    Task SetResolvedAsync(ReviewThread thread, bool resolved, CancellationToken ct = default);

    /// <summary>Submits a review with its line comments in one go.</summary>
    /// <param name="headSha">The pull request head the review (and its comments' line numbers) refer to.</param>
    Task SubmitReviewAsync(int number, ReviewVerdict verdict, string body, IReadOnlyList<DraftComment> comments, string headSha, CancellationToken ct = default);
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
