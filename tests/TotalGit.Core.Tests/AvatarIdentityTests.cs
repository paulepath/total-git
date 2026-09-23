using TotalGit.Core.Avatars;

namespace TotalGit.Core.Tests;

public class AvatarIdentityTests
{
    [Theory]
    [InlineData("12345+octocat@users.noreply.github.com", "https://avatars.githubusercontent.com/u/12345?s=64")]
    [InlineData("octocat@users.noreply.github.com", "https://github.com/octocat.png?size=64")]
    [InlineData("someone@example.com", null)]
    public void GitHub_noreply_addresses_map_to_avatar_urls(string email, string? expected) =>
        Assert.Equal(expected, AvatarIdentity.GitHubNoReplyAvatarUrl(email));

    [Theory]
    [InlineData("https://github.com/dotnet/runtime.git", "dotnet", "runtime")]
    [InlineData("git@github.com:owner/my.repo.git", "owner", "my.repo")]
    [InlineData("https://github.com/owner/repo", "owner", "repo")]
    public void Parses_github_remotes(string url, string owner, string repo) =>
        Assert.Equal((owner, repo), AvatarIdentity.ParseGitHubRemote(url));

    [Fact]
    public void Non_github_remote_is_null() =>
        Assert.Null(AvatarIdentity.ParseGitHubRemote("https://dev.azure.com/org/proj/_git/repo"));

    [Theory]
    [InlineData("Ada Lovelace", "AL")]
    [InlineData("paul", "P")]
    [InlineData("john.q.public", "JP")]
    [InlineData("  ", "?")]
    public void Initials(string name, string expected) => Assert.Equal(expected, AvatarIdentity.Initials(name));

    [Fact]
    public void Gravatar_hash_is_case_and_whitespace_insensitive() =>
        Assert.Equal(AvatarIdentity.GravatarUrl(" Foo@Example.com "), AvatarIdentity.GravatarUrl("foo@example.com"));
}
