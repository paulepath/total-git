using TotalGit.Core.Hosting;

namespace TotalGit.Core.Tests;

public class RemoteHostTests
{
    [Theory]
    [InlineData("https://github.com/paulepath/total-git.git")]
    [InlineData("https://github.com/paulepath/total-git")]
    [InlineData("https://github.com/paulepath/total-git/")]
    [InlineData("git@github.com:paulepath/total-git.git")]
    [InlineData("ssh://git@github.com/paulepath/total-git.git")]
    public void Parses_github_remotes(string url)
    {
        var host = RemoteHostParser.Parse(url);
        Assert.Equal(new RemoteHostInfo(HostKind.GitHub, "github.com", "paulepath", "total-git", "origin"), host);
        Assert.Equal("https://github.com/paulepath/total-git/pull/7", host!.PullRequestUrl(7));
    }

    [Theory]
    [InlineData("https://dev.azure.com/contoso/Web%20Apps/_git/portal")]
    [InlineData("https://contoso@dev.azure.com/contoso/Web%20Apps/_git/portal")]
    [InlineData("git@ssh.dev.azure.com:v3/contoso/Web%20Apps/portal")]
    public void Parses_azure_devops_remotes(string url)
    {
        var host = RemoteHostParser.Parse(url);
        Assert.Equal(HostKind.AzureDevOps, host?.Kind);
        Assert.Equal("contoso/Web%20Apps", host!.Owner);
        Assert.Equal("portal", host.Repo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D:/repos/local-remote.git")]
    [InlineData("https://example.com/team/repo.git")]
    public void Unknown_remotes_are_null(string? url) => Assert.Null(RemoteHostParser.Parse(url));

    [Fact]
    public void Prefers_upstream_then_origin()
    {
        var remotes = new Dictionary<string, string>
        {
            ["mine"] = "https://github.com/me/fork.git",
            ["origin"] = "https://github.com/me/repo.git",
            ["upstream"] = "https://github.com/org/repo.git",
        };
        Assert.Equal("org", RemoteHostParser.Resolve(remotes)?.Owner);
        remotes.Remove("upstream");
        Assert.Equal("repo", RemoteHostParser.Resolve(remotes)?.Repo);
        Assert.Equal("origin", RemoteHostParser.Resolve(remotes)?.RemoteName);
    }

    private sealed class CountingSource(string? token) : ICredentialSource
    {
        public int Calls { get; private set; }

        public string? GetToken(string host)
        {
            Calls++;
            return token;
        }
    }

    [Fact]
    public void Chained_credentials_use_the_first_answer_and_cache_it_until_invalidated()
    {
        var none = new CountingSource(null);
        var gh = new CountingSource("tok");
        var chain = new ChainedCredentialSource(none, gh);

        Assert.Equal("tok", chain.GetToken("github.com"));
        Assert.Equal("tok", chain.GetToken("GitHub.com"));
        Assert.Equal(1, gh.Calls);

        chain.Invalidate();
        chain.GetToken("github.com");
        Assert.Equal(2, gh.Calls);
    }

    [Fact]
    public void Env_source_only_answers_for_its_host()
    {
        var name = "TOTALGIT_TEST_TOKEN_" + Guid.NewGuid().ToString("N")[..6];
        Environment.SetEnvironmentVariable(name, "abc");
        try
        {
            var source = new EnvTokenSource("github.com", name);
            Assert.Equal("abc", source.GetToken("github.com"));
            Assert.Null(source.GetToken("dev.azure.com"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
