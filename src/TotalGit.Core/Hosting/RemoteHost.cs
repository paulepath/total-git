using System.Text.RegularExpressions;

namespace TotalGit.Core.Hosting;

/// <summary>The kind of service a remote is hosted on.</summary>
public enum HostKind { GitHub, AzureDevOps, GitLab }

/// <summary>
/// Where a repository lives on a hosting service, from its remote URL.
/// For Azure DevOps, <see cref="Owner"/> is <c>organization/project</c>.
/// </summary>
public sealed record RemoteHostInfo(HostKind Kind, string Host, string Owner, string Repo, string RemoteName)
{
    /// <summary>The repository's web page.</summary>
    public string WebUrl => Kind switch
    {
        HostKind.AzureDevOps => $"https://{Host}/{Owner}/_git/{Repo}",
        _ => $"https://{Host}/{Owner}/{Repo}",
    };

    /// <summary>A pull request's web page.</summary>
    public string PullRequestUrl(int number) => Kind switch
    {
        HostKind.AzureDevOps => $"{WebUrl}/pullrequest/{number}",
        HostKind.GitLab => $"{WebUrl}/-/merge_requests/{number}",
        _ => $"{WebUrl}/pull/{number}",
    };
}

/// <summary>Recognises hosting services from remote URLs (https and ssh forms).</summary>
public static partial class RemoteHostParser
{
    [GeneratedRegex(@"github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHub();

    // https://dev.azure.com/org/project/_git/repo, https://org@dev.azure.com/org/project/_git/repo,
    // git@ssh.dev.azure.com:v3/org/project/repo
    [GeneratedRegex(@"dev\.azure\.com/(?<org>[^/]+)/(?<project>[^/]+)/_git/(?<repo>[^/]+?)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex AzureDevOpsHttps();

    [GeneratedRegex(@"ssh\.dev\.azure\.com:v3/(?<org>[^/]+)/(?<project>[^/]+)/(?<repo>[^/]+?)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex AzureDevOpsSsh();

    [GeneratedRegex(@"gitlab\.com[:/](?<path>.+?)/(?<repo>[^/]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitLab();

    /// <summary>The host of one remote URL, or null when it isn't a recognised service.</summary>
    public static RemoteHostInfo? Parse(string? url, string remoteName = "origin")
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (GitHub().Match(url) is { Success: true } gh)
            return new(HostKind.GitHub, "github.com", gh.Groups["owner"].Value, gh.Groups["repo"].Value, remoteName);
        var ado = AzureDevOpsHttps().Match(url);
        if (!ado.Success) ado = AzureDevOpsSsh().Match(url);
        if (ado.Success)
            return new(HostKind.AzureDevOps, "dev.azure.com", $"{ado.Groups["org"].Value}/{ado.Groups["project"].Value}", Uri.UnescapeDataString(ado.Groups["repo"].Value), remoteName);
        if (GitLab().Match(url) is { Success: true } gl)
            return new(HostKind.GitLab, "gitlab.com", gl.Groups["path"].Value, gl.Groups["repo"].Value, remoteName);
        return null;
    }

    /// <summary>
    /// The host pull requests are opened on: the <c>upstream</c> remote when it's recognised (fork workflows open
    /// pull requests against upstream), else <c>origin</c>, else the first recognised remote.
    /// </summary>
    public static RemoteHostInfo? Resolve(IEnumerable<KeyValuePair<string, string>> remotes)
    {
        var parsed = remotes.Select(r => Parse(r.Value, r.Key)).OfType<RemoteHostInfo>().ToList();
        return parsed.FirstOrDefault(h => h.RemoteName == "upstream")
            ?? parsed.FirstOrDefault(h => h.RemoteName == "origin")
            ?? parsed.FirstOrDefault();
    }
}
