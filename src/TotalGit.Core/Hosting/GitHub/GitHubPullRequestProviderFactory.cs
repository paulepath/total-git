using System.Collections.Concurrent;

namespace TotalGit.Core.Hosting.GitHub;

/// <summary>
/// Creates <see cref="GitHubPullRequestProvider"/>s for github.com repositories. Providers share one HTTP client and,
/// per host, one record of the token's rate limits.
/// </summary>
/// <param name="credentials">Supplies the GitHub token, asked before every request.</param>
/// <param name="http">Client to use (tests pass a fake handler); by default the factory creates and owns one.</param>
public sealed class GitHubPullRequestProviderFactory(ICredentialSource credentials, HttpClient? http = null) : IPullRequestProviderFactory, IDisposable
{
    private readonly bool _ownsHttp = http is null;
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ConcurrentDictionary<string, GitHubHttp> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A provider for a github.com repository; null for other hosts (GitHub Enterprise isn't supported yet).</summary>
    public IPullRequestProvider? TryCreate(RemoteHostInfo host)
    {
        if (host.Kind != HostKind.GitHub || !string.Equals(host.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return null;
        return new GitHubPullRequestProvider(host, _hosts.GetOrAdd(host.Host, h => new GitHubHttp(_http, credentials, h)));
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
