namespace TotalGit.Core.Hosting.GitHub;

/// <summary>
/// GraphQL documents sent to GitHub. Lists are capped with <c>first:N</c> rather than paged: 30 open pull requests,
/// 20 review requests, 50 reviewers, 100 checks, 100 threads of 100 comments, 100 conversation comments and reviews.
/// </summary>
internal static class GitHubQueries
{
    public const string Viewer = "query { viewer { login } }";

    /// <summary>An author's fields: only a user has a name and an id (bots, apps and organisations don't).</summary>
    private const string Actor = "__typename login avatarUrl ... on User { name databaseId }";

    /// <summary>
    /// A line comment's fields: who may do what with it, whether it's hidden, and its reactions with a few of the
    /// people behind each.
    /// </summary>
    private const string ReviewComment = $$"""
        id body createdAt url authorAssociation lastEditedAt isMinimized minimizedReason
        viewerCanUpdate viewerCanDelete viewerCanMinimize viewerCanReact
        author { {{Actor}} } commit { oid } originalCommit { oid }
        reactionGroups { content viewerHasReacted reactors(first: 5) { totalCount nodes {
          __typename ... on User { login name } ... on Bot { login } ... on Organization { login name } ... on Mannequin { login }
        } } }
        """;

    private const string SummaryFields = $$"""
        number title isDraft state baseRefName headRefName headRefOid isCrossRepository updatedAt url reviewDecision
        author { {{Actor}} }
        reviewRequests(first: 20) { nodes { requestedReviewer { __typename ... on User { login avatarUrl name } ... on Team { name avatarUrl } } } }
        """;

    public const string ListOpen = $$"""
        query($owner: String!, $repo: String!) {
          viewer { login }
          repository(owner: $owner, name: $repo) {
            pullRequests(states: OPEN, first: 30, orderBy: {field: UPDATED_AT, direction: DESC}) {
              nodes {
                {{SummaryFields}}
                createdAt additions deletions changedFiles mergeStateStatus mergeable
                commits(last: 1) { nodes { commit { statusCheckRollup { state contexts(first: 100) { nodes {
                  __typename
                  ... on CheckRun { name status conclusion detailsUrl title startedAt checkSuite { workflowRun { databaseId workflow { name } } } }
                  ... on StatusContext { context state targetUrl description }
                } } } } } }
                latestReviews(first: 20) { nodes { state author { {{Actor}} } } }
              }
            }
          }
        }
        """;

    /// <summary>
    /// The viewer's pull requests in every repository, for notifications: three searches (asked to review, assigned,
    /// their own), the last with the reviews, comments and checks that notifications are about.
    /// </summary>
    public const string Inbox = """
        query($requested: String!, $assigned: String!, $mine: String!) {
          viewer { login }
          requested: search(query: $requested, type: ISSUE, first: 50) { nodes { ...InboxPr } }
          assigned: search(query: $assigned, type: ISSUE, first: 50) { nodes { ...InboxPr } }
          mine: search(query: $mine, type: ISSUE, first: 50) { nodes { ...InboxPr ...InboxActivity } }
        }
        fragment InboxPr on PullRequest {
          number title url isDraft headRefOid
          repository { nameWithOwner }
          author { __typename login }
          timelineItems(itemTypes: [REVIEW_REQUESTED_EVENT, ASSIGNED_EVENT], last: 20) {
            nodes {
              ... on ReviewRequestedEvent { id requestedReviewer { ... on User { login } } }
              ... on AssignedEvent { id assignee { ... on User { login } } }
            }
          }
        }
        fragment InboxActivity on PullRequest {
          commits(last: 1) { nodes { commit { statusCheckRollup { state } } } }
          reviews(last: 30) { nodes { id state body author { __typename login } } }
          comments(last: 20) { nodes { id author { __typename login } } }
          reviewThreads(last: 30) { nodes { comments(last: 5) { nodes { id author { __typename login } } } } }
        }
        """;

    /// <summary>The searches behind <see cref="Inbox"/>: open pull requests in repositories that aren't archived.</summary>
    public const string InboxRequested = "is:pr is:open archived:false user-review-requested:@me";
    public const string InboxAssigned = "is:pr is:open archived:false assignee:@me";
    public const string InboxMine = "is:pr is:open archived:false author:@me";

    // isRequired needs the pull request number to know which branch rules apply.
    public const string Details = $$"""
        query($owner: String!, $repo: String!, $n: Int!) {
          viewer { login avatarUrl name databaseId }
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $n) {
              {{SummaryFields}}
              body baseRefOid mergeStateStatus mergeable additions deletions changedFiles viewerDidAuthor
              commits(last: 1) {
                totalCount
                nodes { commit { statusCheckRollup { state contexts(first: 100) { nodes {
                  __typename
                  ... on CheckRun { name status conclusion detailsUrl title isRequired(pullRequestNumber: $n) startedAt checkSuite { workflowRun { databaseId workflow { name } } } }
                  ... on StatusContext { context state targetUrl description isRequired(pullRequestNumber: $n) }
                } } } } }
              }
              latestReviews(first: 50) { nodes { state author { {{Actor}} } } }
              reviewThreads(first: 100) { nodes {
                id isResolved isOutdated path line startLine originalLine originalStartLine diffSide viewerCanReply viewerCanResolve viewerCanUnresolve
                resolvedBy { {{Actor}} }
                hunk: comments(first: 1) { nodes { diffHunk } }
                comments(first: 100) { nodes { {{ReviewComment}} } }
              } }
              comments(first: 100) { nodes { id body createdAt author { {{Actor}} } } }
              reviews(first: 100) { nodes { id body state submittedAt createdAt author { {{Actor}} } commit { oid } } }
              id
              files(first: 100) { nodes { path viewerViewedState } pageInfo { hasNextPage endCursor } }
            }
          }
        }
        """;

    public const string Reply = """
        mutation($id: ID!, $body: String!) {
          addPullRequestReviewThreadReply(input: {pullRequestReviewThreadId: $id, body: $body}) { comment { id } }
        }
        """;

    /// <summary>More of a pull request's files (after the first 100 that come with its details).</summary>
    public const string Files = """
        query($owner: String!, $repo: String!, $n: Int!, $after: String!) {
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $n) { files(first: 100, after: $after) { nodes { path viewerViewedState } pageInfo { hasNextPage endCursor } } }
          }
        }
        """;

    public const string PullRequestId = """
        query($owner: String!, $repo: String!, $n: Int!) { repository(owner: $owner, name: $repo) { pullRequest(number: $n) { id } } }
        """;

    public const string MarkViewed = "mutation($id: ID!, $path: String!) { markFileAsViewed(input: {pullRequestId: $id, path: $path}) { pullRequest { id } } }";

    public const string UnmarkViewed = "mutation($id: ID!, $path: String!) { unmarkFileAsViewed(input: {pullRequestId: $id, path: $path}) { pullRequest { id } } }";

    public const string Resolve = "mutation($id: ID!) { resolveReviewThread(input: {threadId: $id}) { thread { id isResolved } } }";

    public const string Unresolve = "mutation($id: ID!) { unresolveReviewThread(input: {threadId: $id}) { thread { id isResolved } } }";

    public const string AddReaction = "mutation($id: ID!, $content: ReactionContent!) { addReaction(input: {subjectId: $id, content: $content}) { reaction { content } } }";

    public const string RemoveReaction = "mutation($id: ID!, $content: ReactionContent!) { removeReaction(input: {subjectId: $id, content: $content}) { reaction { content } } }";

    public const string EditComment = """
        mutation($id: ID!, $body: String!) {
          updatePullRequestReviewComment(input: {pullRequestReviewCommentId: $id, body: $body}) { pullRequestReviewComment { id } }
        }
        """;

    public const string DeleteComment = "mutation($id: ID!) { deletePullRequestReviewComment(input: {id: $id}) { pullRequestReview { id } } }";

    public const string Hide = "mutation($id: ID!, $reason: ReportedContentClassifiers!) { minimizeComment(input: {subjectId: $id, classifier: $reason}) { minimizedComment { isMinimized } } }";

    public const string Unhide = "mutation($id: ID!) { unminimizeComment(input: {subjectId: $id}) { unminimizedComment { isMinimized } } }";

    public const string Edits = $$"""
        query($id: ID!) {
          node(id: $id) { ... on PullRequestReviewComment { userContentEdits(first: 30) { nodes { editedAt createdAt editor { {{Actor}} } } } } }
        }
        """;

    public const string MergeOptions = """
        query($owner: String!, $repo: String!) {
          repository(owner: $owner, name: $repo) {
            mergeCommitAllowed squashMergeAllowed rebaseMergeAllowed deleteBranchOnMerge viewerDefaultMergeMethod
          }
        }
        """;

    public const string Mentionable = """
        query($owner: String!, $repo: String!, $q: String!) {
          repository(owner: $owner, name: $repo) { mentionableUsers(first: 8, query: $q) { nodes { login name avatarUrl databaseId } } }
        }
        """;
}
