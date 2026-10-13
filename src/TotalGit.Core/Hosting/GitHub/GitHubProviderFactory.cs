using System.Collections.Concurrent;

namespace TotalGit.Core.Hosting.GitHub;

/// <summary>
/// Creates the pull request and workflow providers for github.com repositories. Providers share one HTTP client
/// and, per host, one record of the token's rate limits.
/// </summary>
/// <param name="credentials">Supplies the GitHub token, asked before every request.</param>
/// <param name="http">Client to use (tests pass a fake handler); by default the factory creates and owns one.</param>
/// <param name="accounts">The GitHub CLI's accounts, for moving to one that can see a repository; by default gh's own.</param>
public sealed class GitHubProviderFactory(ICredentialSource credentials, HttpClient? http = null, IGitHubAccounts? accounts = null)
    : IPullRequestProviderFactory, IWorkflowProviderFactory, IHostAccountSwitcher, IDisposable
{
    private readonly bool _ownsHttp = http is null;
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ConcurrentDictionary<string, GitHubHttp> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly IGitHubAccounts _accounts = accounts ?? new GhCliAccounts();
    // One search at a time: a fetch and the pull request list often fail together.
    private readonly SemaphoreSlim _switching = new(1, 1);

    /// <summary>A provider for a github.com repository; null for other hosts (GitHub Enterprise isn't supported yet).</summary>
    public IPullRequestProvider? TryCreate(RemoteHostInfo host) =>
        IsGitHub(host) ? new GitHubPullRequestProvider(host, Http(host)) : null;

    /// <summary>GitHub Actions for a github.com repository; null for other hosts.</summary>
    public IWorkflowProvider? TryCreateWorkflows(RemoteHostInfo host) =>
        IsGitHub(host) ? new GitHubWorkflowProvider(host, Http(host)) : null;

    private static bool IsGitHub(RemoteHostInfo host) =>
        host.Kind == HostKind.GitHub && string.Equals(host.Host, "github.com", StringComparison.OrdinalIgnoreCase);

    private GitHubHttp Http(RemoteHostInfo host) => _hosts.GetOrAdd(host.Host, h => new GitHubHttp(_http, credentials, h));

    /// <summary>
    /// GitHub answers "not found" for a private repository the account can't see, so with several gh accounts the
    /// active one may simply be the wrong one: tries each (the active one first) and switches to the first that can
    /// see the repository.
    /// </summary>
    public async Task<AccountSwitch?> UseAccountThatCanSeeAsync(RemoteHostInfo host, CancellationToken ct = default)
    {
        if (!IsGitHub(host)) return null;
        await _switching.WaitAsync(ct);
        try
        {
            var signedIn = await _accounts.ListAsync(host.Host, ct);
            if (signedIn.Count < 2) return null;
            var active = signedIn.FirstOrDefault(a => a.Active)?.Login;
            foreach (var account in signedIn.OrderByDescending(a => a.Active))
            {
                if (await _accounts.TokenAsync(host.Host, account.Login, ct) is not { } token || !await CanSeeAsync(host, token, ct)) continue;
                if (account.Active) return new AccountSwitch(account.Login, null); // e.g. another caller already switched
                if (!await _accounts.SwitchAsync(host.Host, account.Login, ct)) return null;
                if (credentials is ChainedCredentialSource chained) chained.Invalidate();
                return new AccountSwitch(account.Login, active);
            }
            return null;
        }
        finally
        {
            _switching.Release();
        }
    }

    private async Task<bool> CanSeeAsync(RemoteHostInfo host, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Uri.EscapeDataString(host.Owner)}/{Uri.EscapeDataString(host.Repo)}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("TotalGit");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        try
        {
            using var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
