namespace TotalGit.Core.Hosting.GitHub;

/// <summary>
/// GraphQL documents sent to GitHub. Lists are capped with <c>first:N</c> rather than paged: 30 open pull requests,
/// 20 review requests, 50 reviewers, 100 checks, 100 threads of 100 comments, 100 conversation comments and reviews.
/// </summary>
internal static class GitHubQueries
{
    public const string Viewer = "query { viewer { login } }";

    private const string SummaryFields = """
        number title isDraft state baseRefName headRefName headRefOid isCrossRepository updatedAt url reviewDecision
        author { login avatarUrl }
        reviewRequests(first: 20) { nodes { requestedReviewer { __typename ... on User { login avatarUrl } ... on Team { name avatarUrl } } } }
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
                  ... on CheckRun { name status conclusion detailsUrl title }
                  ... on StatusContext { context state targetUrl description }
                } } } } } }
                latestReviews(first: 20) { nodes { state author { login avatarUrl } } }
              }
            }
          }
        }
        """;

    // isRequired needs the pull request number to know which branch rules apply.
    public const string Details = $$"""
        query($owner: String!, $repo: String!, $n: Int!) {
          viewer { login }
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $n) {
              {{SummaryFields}}
              body baseRefOid mergeStateStatus mergeable additions deletions changedFiles viewerDidAuthor
              commits(last: 1) {
                totalCount
                nodes { commit { statusCheckRollup { state contexts(first: 100) { nodes {
                  __typename
                  ... on CheckRun { name status conclusion detailsUrl title isRequired(pullRequestNumber: $n) }
                  ... on StatusContext { context state targetUrl description isRequired(pullRequestNumber: $n) }
                } } } } }
              }
              latestReviews(first: 50) { nodes { state author { login avatarUrl } } }
              reviewThreads(first: 100) { nodes {
                id isResolved isOutdated path line startLine originalLine originalStartLine diffSide viewerCanReply viewerCanResolve viewerCanUnresolve
                comments(first: 100) { nodes { id body createdAt author { login avatarUrl } commit { oid } originalCommit { oid } } }
              } }
              comments(first: 100) { nodes { id body createdAt author { login avatarUrl } } }
              reviews(first: 100) { nodes { id body state submittedAt createdAt author { login avatarUrl } } }
            }
          }
        }
        """;

    public const string Reply = """
        mutation($id: ID!, $body: String!) {
          addPullRequestReviewThreadReply(input: {pullRequestReviewThreadId: $id, body: $body}) { comment { id } }
        }
        """;

    public const string Resolve = "mutation($id: ID!) { resolveReviewThread(input: {threadId: $id}) { thread { id isResolved } } }";

    public const string Unresolve = "mutation($id: ID!) { unresolveReviewThread(input: {threadId: $id}) { thread { id isResolved } } }";
}
