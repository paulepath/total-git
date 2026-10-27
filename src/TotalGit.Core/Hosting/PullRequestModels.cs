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

/// <summary>How a pull request's commits go into its target branch.</summary>
public enum MergeMethod
{
    /// <summary>A merge commit joins the branch, keeping its commits.</summary>
    Merge,

    /// <summary>All the changes as one new commit.</summary>
    Squash,

    /// <summary>Each commit replayed on top of the target.</summary>
    Rebase,
}

/// <summary>How a repository lets pull requests be merged.</summary>
/// <param name="Methods">The methods it allows, in GitHub's order (merge commit, squash, rebase).</param>
/// <param name="Default">The method to offer first: the one the signed-in user last used here.</param>
/// <param name="DeletesBranchOnMerge">The host deletes a pull request's branch itself once it's merged.</param>
public sealed record MergeOptions(IReadOnlyList<MergeMethod> Methods, MergeMethod Default, bool DeletesBranchOnMerge);

/// <summary>Which side of the diff a line comment is on: the base (old) or the pull request (new) version.</summary>
public enum DiffSide { Left, Right }

/// <param name="Name">The name on their profile, when they've set one (bots and organisations have none).</param>
public sealed record PrUser(string Login, string? AvatarUrl, string? Name = null)
{
    /// <summary>An app or bot account (GitHub's "[bot]" users), not a person.</summary>
    public bool IsBot { get; init; }

    /// <summary>The host's numeric id for a person, when known (GitHub's noreply commit address uses it).</summary>
    public long? Id { get; init; }

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
    /// <summary>The signed-in user is asked to review it, directly or (when the host says) through a team.</summary>
    bool ViewerReviewRequested,
    string Url)
{
    /// <summary>Who is asked to review it, then who else has reviewed it, with their latest review's state.</summary>
    public IReadOnlyList<Reviewer> Reviewers { get; init; } = [];

    /// <summary>The signed-in user opened it.</summary>
    public bool IsViewerAuthor { get; init; }

    /// <summary>
    /// The signed-in user has reviewed it but doesn't approve it now: they only commented, asked for changes, or their
    /// approval was dismissed.
    /// </summary>
    public bool ViewerReviewedNotApproved { get; init; }

    /// <summary>
    /// Still waiting for the signed-in user's approval: open, someone else's, and they're asked to review it or have
    /// reviewed it without approving. Hosts drop the request once someone reviews, so asked alone isn't enough.
    /// </summary>
    public bool WaitsForViewer => State == PullRequestState.Open && !IsViewerAuthor && (ViewerReviewRequested || ViewerReviewedNotApproved);

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

/// <summary>How the author of a comment is connected to the repository (shown as a badge, as on GitHub).</summary>
public enum AuthorRole { None, Owner, Member, Collaborator, Contributor, FirstTimeContributor }

/// <summary>The reactions hosts offer on comments (GitHub's eight).</summary>
public enum Reaction { ThumbsUp, ThumbsDown, Laugh, Hooray, Confused, Heart, Rocket, Eyes }

/// <summary>Why a comment was hidden (GitHub's "minimized" reasons).</summary>
public enum HideReason { Spam, Abuse, OffTopic, Outdated, Duplicate, Resolved }

/// <summary>One kind of reaction on a comment.</summary>
/// <param name="Reactors">Some of the people who reacted; <paramref name="Count"/> counts them all.</param>
public sealed record ReactionGroup(Reaction Reaction, int Count, bool ViewerReacted, IReadOnlyList<PrUser> Reactors);

/// <summary>A version of a comment: who saved it and when.</summary>
public sealed record CommentEdit(PrUser Editor, DateTimeOffset At);

public sealed record PrComment(string Id, PrUser Author, string Body, DateTimeOffset CreatedAt)
{
    /// <summary>The comment's web address.</summary>
    public string? Url { get; init; }

    public AuthorRole Role { get; init; }

    /// <summary>When it was last edited; null when it never was.</summary>
    public DateTimeOffset? EditedAt { get; init; }

    /// <summary>Hidden (minimized) by someone with write access, and why.</summary>
    public bool IsHidden { get; init; }
    public HideReason? HiddenReason { get; init; }

    public IReadOnlyList<ReactionGroup> Reactions { get; init; } = [];

    /// <summary>What the signed-in user may do with it.</summary>
    public bool ViewerCanEdit { get; init; }
    public bool ViewerCanDelete { get; init; }
    public bool ViewerCanHide { get; init; }
    public bool ViewerCanReact { get; init; }
}

/// <summary>A conversation about one place in the diff: a line comment and its replies.</summary>
/// <param name="IsOutdated">The lines it was made on have changed since.</param>
public sealed record ReviewThread(
    string Id,
    CommentAnchor Anchor,
    bool IsResolved,
    bool IsOutdated,
    IReadOnlyList<PrComment> Comments,
    bool ViewerCanReply,
    bool ViewerCanResolve)
{
    /// <summary>Who resolved it, when it's resolved and the host says.</summary>
    public PrUser? ResolvedBy { get; init; }

    /// <summary>
    /// The diff the first comment was made on, as a unified-diff hunk ending at the commented line (shows outdated
    /// comments in their original code, and gives a suggestion its replaced lines).
    /// </summary>
    public string? DiffHunk { get; init; }
}

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

    /// <summary>The signed-in user, with their avatar, when the host said.</summary>
    public PrUser? Viewer { get; init; }
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
