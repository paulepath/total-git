using System.Net;
using TotalGit.Core.Git;
using TotalGit.Core.Hosting;
using TotalGit.Core.Hosting.GitHub;
using TotalGit.Core.Tests.Fakes;

namespace TotalGit.Core.Tests;

public class GitHubAccountSwitchTests
{
    private const string RepoUrl = "https://api.github.com/repos/octo/widgets";
    private static readonly RemoteHostInfo Repo = new(HostKind.GitHub, "github.com", "octo", "widgets", "origin");

    /// <summary>gh's accounts: each login's token is "token-{login}".</summary>
    private sealed class FakeAccounts(params GitHubAccount[] accounts) : IGitHubAccounts
    {
        public List<GitHubAccount> Accounts { get; } = [.. accounts];
        public List<string> Switches { get; } = [];
        public bool RefuseSwitch { get; init; }

        public Task<IReadOnlyList<GitHubAccount>> ListAsync(string host, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GitHubAccount>>(Accounts.ToList());

        public Task<string?> TokenAsync(string host, string login, CancellationToken ct) => Task.FromResult<string?>($"token-{login}");

        public Task<bool> SwitchAsync(string host, string login, CancellationToken ct)
        {
            if (RefuseSwitch) return Task.FromResult(false);
            Switches.Add(login);
            for (var i = 0; i < Accounts.Count; i++) Accounts[i] = Accounts[i] with { Active = Accounts[i].Login == login };
            return Task.FromResult(true);
        }
    }

    /// <summary>Counts token lookups, so a switch can be seen to make Total Git ask again.</summary>
    private sealed class CountingSource : ICredentialSource
    {
        public int Asked { get; private set; }

        public string? GetToken(string host)
        {
            Asked++;
            return "t";
        }
    }

    /// <summary>The repository is visible only with the tokens of <paramref name="canSee"/>.</summary>
    private static FakeHttpHandler Visible(params string[] canSee)
    {
        var http = new FakeHttpHandler();
        http.Routes[RepoUrl] = request => canSee.Contains(request.Headers.Authorization?.Parameter?["token-".Length..])
            ? FakeHttpHandler.Json("""{"full_name":"octo/widgets"}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        return http;
    }

    [Fact]
    public async Task Switches_to_the_account_that_can_see_the_repository()
    {
        var accounts = new FakeAccounts(new("work", Active: true), new("client", Active: false));
        var credentials = new ChainedCredentialSource(new CountingSource());
        credentials.GetToken("github.com");
        using var factory = new GitHubProviderFactory(credentials, new HttpClient(Visible("client")), accounts);

        var switched = await factory.UseAccountThatCanSeeAsync(Repo);

        Assert.Equal(new AccountSwitch("client", "work"), switched);
        Assert.Equal(["client"], accounts.Switches);
    }

    [Fact]
    public async Task A_switch_makes_Total_Git_ask_for_the_token_again()
    {
        var source = new CountingSource();
        var credentials = new ChainedCredentialSource(source);
        credentials.GetToken("github.com");
        using var factory = new GitHubProviderFactory(credentials, new HttpClient(Visible("client")),
            new FakeAccounts(new("work", Active: true), new("client", Active: false)));

        await factory.UseAccountThatCanSeeAsync(Repo);
        credentials.GetToken("github.com");

        Assert.Equal(2, source.Asked);
    }

    [Fact]
    public async Task When_the_active_account_can_see_it_nothing_is_switched()
    {
        var accounts = new FakeAccounts(new("work", Active: true), new("client", Active: false));
        using var factory = new GitHubProviderFactory(new ChainedCredentialSource(), new HttpClient(Visible("work", "client")), accounts);

        Assert.Equal(new AccountSwitch("work", null), await factory.UseAccountThatCanSeeAsync(Repo));
        Assert.Empty(accounts.Switches);
    }

    [Fact]
    public async Task No_account_that_can_see_it_means_no_switch()
    {
        var accounts = new FakeAccounts(new("work", Active: true), new("client", Active: false));
        using var factory = new GitHubProviderFactory(new ChainedCredentialSource(), new HttpClient(Visible()), accounts);

        Assert.Null(await factory.UseAccountThatCanSeeAsync(Repo));
        Assert.Empty(accounts.Switches);
    }

    [Fact]
    public async Task With_one_account_there_is_nothing_to_try()
    {
        var http = Visible("work");
        using var factory = new GitHubProviderFactory(new ChainedCredentialSource(), new HttpClient(http), new FakeAccounts(new GitHubAccount("work", Active: true)));

        Assert.Null(await factory.UseAccountThatCanSeeAsync(Repo));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task A_refused_switch_is_no_switch()
    {
        var accounts = new FakeAccounts(new("work", Active: true), new("client", Active: false)) { RefuseSwitch = true };
        using var factory = new GitHubProviderFactory(new ChainedCredentialSource(), new HttpClient(Visible("client")), accounts);

        Assert.Null(await factory.UseAccountThatCanSeeAsync(Repo));
    }

    [Fact]
    public async Task Only_github_com_switches()
    {
        using var factory = new GitHubProviderFactory(new ChainedCredentialSource(), new HttpClient(Visible("client")),
            new FakeAccounts(new("work", Active: true), new("client", Active: false)));

        Assert.Null(await factory.UseAccountThatCanSeeAsync(new RemoteHostInfo(HostKind.AzureDevOps, "dev.azure.com", "org/proj", "r", "origin")));
    }

    [Theory]
    [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/octo/widgets.git/' not found", "https://github.com/octo/widgets.git/")]
    [InlineData("fatal: repository 'https://github.com/octo/widgets/' not found", "https://github.com/octo/widgets/")]
    [InlineData("ERROR: Repository not found.\nfatal: Could not read from remote repository.", null)] // ssh: keys, not gh
    [InlineData("fatal: repository '/d/repos/gone' does not exist", null)]
    [InlineData("error: failed to push some refs", null)]
    public void Recognises_git_saying_a_repository_is_not_found(string message, string? url)
    {
        Assert.Equal(url, GitActions.RepositoryNotFoundUrl(message));
        if (url is not null) Assert.Equal(Repo, RemoteHostParser.Parse(url));
    }
}
