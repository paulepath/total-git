using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TotalGit.Core.Avatars;

/// <summary>
/// Resolves author avatar images, preferring GitHub: the GitHub noreply address, then the GitHub
/// commit author (when the repo is hosted on GitHub), then a GitHub user search by email, and only
/// then Gravatar. Returns null when nothing is found so callers can show an initials badge.
/// </summary>
/// <remarks>
/// GitHub images are cached permanently. Gravatar images are cached as a fallback only: while
/// GitHub couldn't answer (rate limit, offline) it is asked again on the next launch, and a match
/// replaces the Gravatar image. Confirmed misses are remembered for a week.
/// </remarks>
public sealed class AvatarService : IDisposable
{
    private static readonly TimeSpan MissExpiry = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly string _missFile;
    private readonly Lazy<string?> _token;
    private readonly SemaphoreSlim _throttle = new(4);
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _inflight = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _misses;
    private readonly Lock _missLock = new();
    // GitHub rate-limits search (30/min) separately from the rest of the API (5000/h with a token).
    private volatile bool _coreApiExhausted;
    private volatile bool _searchApiExhausted;

    /// <param name="cacheDir">Folder for cached images and the miss list.</param>
    /// <param name="http">Client to use (tests pass a fake handler).</param>
    /// <param name="gitHubToken">Supplies a GitHub token; defaults to GITHUB_TOKEN, GH_TOKEN or <c>gh auth token</c>.</param>
    public AvatarService(string cacheDir, HttpClient? http = null, Func<string?>? gitHubToken = null)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
        _missFile = Path.Combine(cacheDir, "misses.json");
        _misses = LoadMisses(_missFile);
        _token = new Lazy<string?>(gitHubToken ?? DefaultGitHubToken);

        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TotalGit/0.1");
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
        var gitHubFile = Path.Combine(_cacheDir, $"gh-{key}.img");
        var gravatarFile = Path.Combine(_cacheDir, $"gv-{key}.img");
        if (File.Exists(gitHubFile)) return await File.ReadAllBytesAsync(gitHubFile);

        await _throttle.WaitAsync();
        try
        {
            if (!IsRecentMiss("gh:" + key))
            {
                var (bytes, answered) = await TryGitHubAsync(email, gitHubRepo, sha);
                if (bytes is not null)
                {
                    await File.WriteAllBytesAsync(gitHubFile, bytes);
                    TryDelete(gravatarFile);
                    return bytes;
                }
                // Only a definite "no GitHub user" is remembered; rate limits and errors retry next time.
                if (answered) RecordMiss("gh:" + key);
            }

            if (File.Exists(gravatarFile)) return await File.ReadAllBytesAsync(gravatarFile);
            if (IsRecentMiss("gv:" + key)) return null;

            var gravatar = await TryDownloadAsync(AvatarIdentity.GravatarUrl(email));
            if (gravatar is not null) await File.WriteAllBytesAsync(gravatarFile, gravatar);
            else RecordMiss("gv:" + key);
            return gravatar;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Offline or transient failure: nothing recorded, so it is retried next launch.
            _inflight.TryRemove(key, out _);
            return File.Exists(gravatarFile) ? await File.ReadAllBytesAsync(gravatarFile) : null;
        }
        finally
        {
            _throttle.Release();
        }
    }

    /// <summary>
    /// Tries every GitHub source. <c>answered</c> is true when GitHub was reachable and gave a
    /// definite answer, i.e. a miss can be cached.
    /// </summary>
    private async Task<(byte[]? Bytes, bool Answered)> TryGitHubAsync(string email, (string Owner, string Repo)? repo, string? sha)
    {
        var bytes = await TryDownloadAsync(AvatarIdentity.GitHubNoReplyAvatarUrl(email));
        if (bytes is not null) return (bytes, true);

        var answered = true;
        var asked = false;

        if (repo is { } gh && sha is not null)
        {
            asked = true;
            var (url, ok) = await GitHubApiAsync($"repos/{gh.Owner}/{gh.Repo}/commits/{sha}", search: false,
                root => root.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object
                    ? AvatarUrl(author)
                    : null);
            answered &= ok;
            if ((bytes = await TryDownloadAsync(url)) is not null) return (bytes, true);
        }

        // Search needs a token: the anonymous search limit is too small to be useful.
        if (_token.Value is not null)
        {
            asked = true;
            var (url, ok) = await GitHubApiAsync($"search/users?q={Uri.EscapeDataString(email + " in:email")}", search: true,
                root => root.TryGetProperty("items", out var items) && items.GetArrayLength() > 0
                    ? AvatarUrl(items[0])
                    : null);
            answered &= ok;
            if ((bytes = await TryDownloadAsync(url)) is not null) return (bytes, true);
        }

        return (null, asked && answered);
    }

    private static string? AvatarUrl(JsonElement user)
    {
        if (!user.TryGetProperty("avatar_url", out var url) || url.GetString() is not { } avatarUrl) return null;
        return avatarUrl + (avatarUrl.Contains('?') ? "&" : "?") + "s=64";
    }

    /// <summary>Calls the GitHub REST API. Returns the extracted value and whether GitHub gave a real answer.</summary>
    private async Task<(string? Value, bool Answered)> GitHubApiAsync(string path, bool search, Func<JsonElement, string?> extract)
    {
        if (search ? _searchApiExhausted : _coreApiExhausted) return (null, false);

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/" + path);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (_token.Value is { } token) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            if (search) _searchApiExhausted = true;
            else _coreApiExhausted = true;
            return (null, false);
        }
        // 404/422: commit not on GitHub or query rejected. A definite answer, not a transient failure.
        if (!resp.IsSuccessStatusCode) return (null, resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity);

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return (extract(doc.RootElement), true);
    }

    private async Task<byte[]?> TryDownloadAsync(string? url)
    {
        if (url is null) return null;
        using var resp = await _http.GetAsync(url);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync() : null;
    }

    /// <summary>GITHUB_TOKEN / GH_TOKEN, else the GitHub CLI's login, else anonymous.</summary>
    private static string? DefaultGitHubToken()
    {
        foreach (var name in (string[])["GITHUB_TOKEN", "GH_TOKEN"])
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } env) return env;

        try
        {
            var psi = new ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(5000)) return null;
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null; // gh not installed
        }
    }

    private bool IsRecentMiss(string key) =>
        _misses.TryGetValue(key, out var at) && DateTimeOffset.UtcNow - at < MissExpiry;

    private void RecordMiss(string key)
    {
        _misses[key] = DateTimeOffset.UtcNow;
        lock (_missLock)
        {
            try { File.WriteAllText(_missFile, JsonSerializer.Serialize(_misses)); }
            catch (IOException) { }
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
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

    public void Dispose()
    {
        _http.Dispose();
        _throttle.Dispose();
    }
}
