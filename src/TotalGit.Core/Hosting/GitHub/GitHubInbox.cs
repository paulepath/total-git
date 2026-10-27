using System.Text.Json.Nodes;

namespace TotalGit.Core.Hosting.GitHub;

/// <summary>The pull requests involving one GitHub account, in every repository: one GraphQL request.</summary>
internal sealed class GitHubInbox(GitHubHttp http) : IPullRequestInbox
{
    public async Task<Inbox> GetAsync(CancellationToken ct = default)
    {
        var data = await http.GraphQLAsync(GitHubQueries.Inbox, new JsonObject
        {
            ["requested"] = GitHubQueries.InboxRequested,
            ["assigned"] = GitHubQueries.InboxAssigned,
            ["mine"] = GitHubQueries.InboxMine,
            ["reviewed"] = GitHubQueries.InboxReviewed,
        }, ct);
        return GitHubMapping.Inbox(data);
    }
}
