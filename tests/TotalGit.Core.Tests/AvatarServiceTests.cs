using System.Net;
using System.Text;
using TotalGit.Core.Avatars;

namespace TotalGit.Core.Tests;

public sealed class AvatarServiceTests : IDisposable
{
    private const string Email = "dev@example.com";
    private static readonly (string, string) Repo = ("owner", "repo");
    private static readonly byte[] GitHubImage = [1, 1, 1];
    private static readonly byte[] GravatarImage = [2, 2, 2];

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "totalgit-tests", "avatars-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir)) Directory.Delete(_cacheDir, recursive: true);
    }

    /// <summary>Serves canned responses by URL prefix and records every request.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var url = request.RequestUri!.ToString();
            var route = Routes.FirstOrDefault(r => url.StartsWith(r.Key, StringComparison.Ordinal));
            return Task.FromResult(route.Value?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };

    private static FakeHandler Handler(bool commitAuthor = true, bool gravatar = true, bool rateLimited = false)
    {
        var h = new FakeHandler();
        h.Routes["https://api.github.com/repos/owner/repo/commits/"] = () => rateLimited
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Json(commitAuthor ? """{"author":{"avatar_url":"https://avatars.githubusercontent.com/u/42?v=4"}}""" : """{"author":null}""");
        h.Routes["https://api.github.com/search/users"] = () => rateLimited
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Json("""{"total_count":0,"items":[]}""");
        h.Routes["https://avatars.githubusercontent.com/u/42"] = () => Bytes(GitHubImage);
        h.Routes["https://www.gravatar.com/avatar/"] = () => gravatar ? Bytes(GravatarImage) : new HttpResponseMessage(HttpStatusCode.NotFound);
        return h;
    }

    private AvatarService Service(FakeHandler handler, string? token = "token") =>
        new(_cacheDir, new HttpClient(handler), () => token);

    [Fact]
    public async Task Prefers_github_commit_author_over_gravatar()
    {
        var handler = Handler();
        using var service = Service(handler);

        var bytes = await service.GetAvatarAsync(Email, Repo, "abc123");

        Assert.Equal(GitHubImage, bytes);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Host == "www.gravatar.com");
    }

    [Fact]
    public async Task Finds_github_user_by_email_when_repo_is_not_on_github()
    {
        var handler = Handler();
        handler.Routes["https://api.github.com/search/users"] = () =>
            Json("""{"total_count":1,"items":[{"avatar_url":"https://avatars.githubusercontent.com/u/42?v=4"}]}""");
        using var service = Service(handler);

        var bytes = await service.GetAvatarAsync(Email, gitHubRepo: null, sampleCommitSha: "abc123");

        Assert.Equal(GitHubImage, bytes);
    }

    [Fact]
    public async Task Falls_back_to_gravatar_when_github_has_no_match()
    {
        using var service = Service(Handler(commitAuthor: false));

        Assert.Equal(GravatarImage, await service.GetAvatarAsync(Email, Repo, "abc123"));
    }

    [Fact]
    public async Task Gravatar_used_while_rate_limited_is_upgraded_to_github_later()
    {
        using (var limited = Service(Handler(rateLimited: true)))
            Assert.Equal(GravatarImage, await limited.GetAvatarAsync(Email, Repo, "abc123"));

        using var later = Service(Handler());

        Assert.Equal(GitHubImage, await later.GetAvatarAsync(Email, Repo, "abc123"));
    }

    [Fact]
    public async Task Github_miss_is_remembered_so_the_api_is_not_asked_again()
    {
        using (var first = Service(Handler(commitAuthor: false)))
            await first.GetAvatarAsync(Email, Repo, "abc123");

        var handler = Handler();
        using var second = Service(handler);
        var bytes = await second.GetAvatarAsync(Email, Repo, "abc123");

        Assert.Equal(GravatarImage, bytes);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Host == "api.github.com");
    }

    [Fact]
    public async Task Token_is_sent_to_github_api_only()
    {
        var handler = Handler(commitAuthor: false);
        using var service = Service(handler, token: "secret");

        await service.GetAvatarAsync(Email, Repo, "abc123");

        Assert.All(handler.Requests.Where(r => r.RequestUri!.Host == "api.github.com"),
            r => Assert.Equal("Bearer secret", r.Headers.Authorization?.ToString()));
        Assert.All(handler.Requests.Where(r => r.RequestUri!.Host != "api.github.com"),
            r => Assert.Null(r.Headers.Authorization));
    }

    [Fact]
    public async Task Search_by_email_is_skipped_without_a_token()
    {
        var handler = Handler(commitAuthor: false);
        using var service = Service(handler, token: null);

        await service.GetAvatarAsync(Email, gitHubRepo: null, sampleCommitSha: null);

        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/search/"));
    }
}
