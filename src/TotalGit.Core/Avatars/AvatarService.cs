using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TotalGit.Core.Avatars;

/// <summary>
/// Resolves author avatar images: GitHub noreply address, then the GitHub commit author (when the
/// repo is hosted on GitHub), then Gravatar. Results, including misses, are cached on disk. Returns
/// null when no image exists so callers can fall back to an initials badge.
/// </summary>
public sealed class AvatarService : IDisposable
{
    private static readonly TimeSpan MissExpiry = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly string _missFile;
    private readonly SemaphoreSlim _throttle = new(4);
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _inflight = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _misses;
    private readonly Lock _missLock = new();
    private volatile bool _gitHubApiExhausted;

    public AvatarService(string cacheDir, HttpClient? http = null)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
        _missFile = Path.Combine(cacheDir, "misses.json");
        _misses = LoadMisses(_missFile);

        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TotalGit/0.1");
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <param name="email">Author email.</param>
    /// <param name="gitHubRepo">owner/repo of the GitHub origin, if any.</param>
    /// <param name="sampleCommitSha">A commit by this author, used for the GitHub API lookup.</param>
    public Task<byte[]?> GetAvatarAsync(string email, (string Owner, string Repo)? gitHubRepo, string? sampleCommitSha)
    {
        var key = AvatarIdentity.CacheKey(email);
        return _inflight.GetOrAdd(key, _ => ResolveAsync(key, email, gitHubRepo, sampleCommitSha));
    }

    private async Task<byte[]?> ResolveAsync(string key, string email, (string Owner, string Repo)? gitHubRepo, string? sha)
    {
        var file = Path.Combine(_cacheDir, key + ".img");
        if (File.Exists(file)) return await File.ReadAllBytesAsync(file);
        if (_misses.TryGetValue(key, out var missedAt) && DateTimeOffset.UtcNow - missedAt < MissExpiry) return null;

        await _throttle.WaitAsync();
        try
        {
            var bytes = await TryDownloadAsync(AvatarIdentity.GitHubNoReplyAvatarUrl(email));

            if (bytes is null && gitHubRepo is { } gh && sha is not null && !_gitHubApiExhausted)
                bytes = await TryDownloadAsync(await LookupGitHubAuthorAvatarAsync(gh, sha));

            bytes ??= await TryDownloadAsync(AvatarIdentity.GravatarUrl(email));

            if (bytes is not null)
            {
                await File.WriteAllBytesAsync(file, bytes);
            }
            else
            {
                _misses[key] = DateTimeOffset.UtcNow;
                SaveMisses();
            }
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Offline or transient failure: not recorded as a miss, so it is retried next launch.
            _inflight.TryRemove(key, out _);
            return null;
        }
        finally
        {
            _throttle.Release();
        }
    }

    private async Task<string?> LookupGitHubAuthorAvatarAsync((string Owner, string Repo) gh, string sha)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{gh.Owner}/{gh.Repo}/commits/{sha}");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await _http.SendAsync(req);

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            _gitHubApiExhausted = true;
            return null;
        }
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        if (doc.RootElement.TryGetProperty("author", out var author)
            && author.ValueKind == JsonValueKind.Object
            && author.TryGetProperty("avatar_url", out var url)
            && url.GetString() is { } avatarUrl)
        {
            return avatarUrl + (avatarUrl.Contains('?') ? "&" : "?") + "s=64";
        }
        return null;
    }

    private async Task<byte[]?> TryDownloadAsync(string? url)
    {
        if (url is null) return null;
        using var resp = await _http.GetAsync(url);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync() : null;
    }

    private static ConcurrentDictionary<string, DateTimeOffset> LoadMisses(string path)
    {
        try
        {
            if (File.Exists(path))
                return new(JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(path)) ?? []);
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new();
    }

    private void SaveMisses()
    {
        lock (_missLock)
        {
            try { File.WriteAllText(_missFile, JsonSerializer.Serialize(_misses)); }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _throttle.Dispose();
    }
}
