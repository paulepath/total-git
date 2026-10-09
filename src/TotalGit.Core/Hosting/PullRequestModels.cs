namespace TotalGit.Core.Hosting;

// Provider-neutral pull request data. Hosting providers (GitHub, and later Azure DevOps or GitLab) map their
// API responses to these records; the app only sees these.

public enum PullRequestState { Open, Closed, Merged }

/// <summary>The combined result of a pull request's checks (CI runs and commit statuses).</summary>
public enum ChecksState { None, Pending, Success, Failure }

/// <summary>Where the pull request stands on reviews, as the host's branch rules see it.</summary>
public enum ReviewDecision { None, ReviewRequired, Approved, ChangesRequested }

/// <summary>Whether the pull request can be merged now, and if not the main reason.</summary>
public enum MergeState { Unknown, Clean, Blocked, Behind, Conflicting, Unstable, Draft }

public enum CheckStatus { Pending, Success, Failure, Neutral, Skipped, Cancelled }

/// <summary>A reviewer's latest review.</summary>
public enum ReviewState { None, Commented, Approved, ChangesRequested, Dismissed, Pending }

public enum ReviewVerdict { Comment, Approve, RequestChanges }

/// <summary>Which side of the diff a line comment is on: the base (old) or the pull request (new) version.</summary>
public enum DiffSide { Left, Right }

/// <param name="Name">The name on their profile, when they've set one (bots and organisations have none).</param>
public sealed record PrUser(string Login, string? AvatarUrl, string? Name = null)
{
    /// <summary>Their name, or their login when they haven't set one.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Login : Name;

    /// <summary>"Name (login)", or just the login when they haven't set a name.</summary>
    public string NameAndLogin => DisplayName == Login ? Login : $"{DisplayName} ({Login})";
}

public sealed record PullRequestSummary(
    int Number,
    string Title,
    PrUser Author,
    bool IsDraft,
    PullRequestState State,
    string BaseRef,
    string HeadRef,
    string HeadSha,
    /// <summary>The head branch is in another repository (a fork), so it isn't on this remote.</summary>
    bool IsCrossRepository,
    DateTimeOffset UpdatedAt,
    ChecksState Checks,
    ReviewDecision ReviewDecision,
    /// <summary>The signed-in user is asked to review it.</summary>
    bool ViewerReviewRequested,
    string Url)
{
    /// <summary>Who is asked to review it, then who else has reviewed it, with their latest review's state.</summary>
    public IReadOnlyList<Reviewer> Reviewers { get; init; } = [];

    /// <summary>The signed-in user opened it.</summary>
    public bool IsViewerAuthor { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Size of the change, when the host reported it.</summary>
    public int? Additions { get; init; }
    public int? Deletions { get; init; }
    public int? ChangedFiles { get; init; }

    /// <summary>Whether it can be merged now (behind its target, conflicting…), when the host has worked it out.</summary>
    public MergeState MergeState { get; init; }

    /// <summary>The latest commit's checks, when listed (for counts and the names of failing ones).</summary>
    public IReadOnlyList<CheckItem> CheckRuns { get; init; } = [];
}

/// <summary>A requested or actual reviewer: a person or a team.</summary>
/// <param name="Name">A person's login or a team's name.</param>
public sealed record Reviewer(string Name, string? AvatarUrl, ReviewState State, bool IsRequested)
{
    /// <summary>A person's profile name, when they've set one.</summary>
    public string? FullName { get; init; }

    /// <summary>Their name, or <see cref="Name"/> when there isn't one.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Name : FullName;
}

/// <summary>Where a line comment is anchored.</summary>
/// <param name="Line">The line on <paramref name="Side"/> in the current diff; null when the comment is outdated.</param>
/// <param name="StartLine">The first line of a multi-line comment.</param>
/// <param name="OriginalLine">The line in the commit the comment was made on.</param>
/// <param name="CommitSha">The commit the comment was made on.</param>
public sealed record CommentAnchor(string Path, DiffSide Side, int? Line, int? StartLine = null, int? OriginalLine = null, string? CommitSha = null);

public sealed record PrComment(string Id, PrUser Author, string Body, DateTimeOffset CreatedAt);

/// <summary>A conversation about one place in the diff: a line comment and its replies.</summary>
/// <param name="IsOutdated">The lines it was made on have changed since.</param>
public sealed record ReviewThread(
    string Id,
    CommentAnchor Anchor,
    bool IsResolved,
    bool IsOutdated,
    IReadOnlyList<PrComment> Comments,
    bool ViewerCanReply,
    bool ViewerCanResolve);

public enum TimelineKind { Comment, Review }

/// <summary>An entry in the pull request's conversation: a general comment, or a submitted review.</summary>
public sealed record TimelineItem(string Id, TimelineKind Kind, PrUser Author, string Body, DateTimeOffset CreatedAt, ReviewState ReviewState = ReviewState.None);

public sealed record CheckItem(string Name, CheckStatus Status, bool IsRequired, string? DetailsUrl, string? Description = null);

public sealed record PullRequestDetails(
    PullRequestSummary Summary,
    string Body,
    /// <summary>The base branch's commit the host compares against (the merge-base is computed locally).</summary>
    string BaseSha,
    MergeState MergeState,
    IReadOnlyList<Reviewer> Reviewers,
    IReadOnlyList<CheckItem> Checks,
    IReadOnlyList<ReviewThread> Threads,
    IReadOnlyList<TimelineItem> Timeline,
    int CommitCount,
    int Additions,
    int Deletions,
    int ChangedFiles,
    /// <summary>The signed-in user opened it (hosts don't let authors approve their own pull requests).</summary>
    bool ViewerIsAuthor)
{
    /// <summary>The host's id for the pull request (GitHub's node id), for calls that address it by id.</summary>
    public string? NodeId { get; init; }

    /// <summary>The signed-in user's "viewed" mark on each changed file, by path (files not listed are unviewed).</summary>
    public IReadOnlyDictionary<string, FileViewState> ViewedFiles { get; init; } = new Dictionary<string, FileViewState>();

    /// <summary>The head commit of the signed-in user's latest submitted review, or null when they haven't reviewed.</summary>
    public string? LastViewerReviewSha { get; init; }

    /// <summary>When the signed-in user's latest review was submitted.</summary>
    public DateTimeOffset? LastViewerReviewAt { get; init; }
}

/// <summary>The signed-in user's "viewed" mark on one file of a pull request.</summary>
public enum FileViewState
{
    Unviewed,
    Viewed,

    /// <summary>Marked viewed, then the file changed (the host clears the mark).</summary>
    ChangedSinceViewed,
}

/// <summary>A line comment waiting to be sent with a review.</summary>
public sealed record DraftComment(CommentAnchor Anchor, string Body);
