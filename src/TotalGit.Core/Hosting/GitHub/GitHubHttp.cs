using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotalGit.Core.Hosting.GitHub;

/// <summary>
/// Sends GitHub API calls with the user's token and turns every failure into a <see cref="HostException"/>.
/// Remembers rate limits (per API resource, e.g. <c>graphql</c> and <c>core</c>) so an exhausted limit fails
/// fast until it resets.
/// </summary>
internal sealed class GitHubHttp(HttpClient http, ICredentialSource credentials, string host)
{
    public const string SignInMessage = "Sign in to GitHub: run `gh auth login`, or set GITHUB_TOKEN.";
    private const string SsoMessage = "Authorise your GitHub token for the organisation's single sign-on (github.com/settings/tokens).";

    private readonly string _apiBase = "https://api.github.com/";
    private readonly ConcurrentDictionary<string, DateTimeOffset> _exhaustedUntil = new();

    /// <summary>Runs a GraphQL query or mutation and returns its <c>data</c>.</summary>
    public async Task<JsonElement> GraphQLAsync(string query, JsonObject variables, CancellationToken ct)
    {
        var root = await SendAsync(HttpMethod.Post, "graphql", new JsonObject { ["query"] = query, ["variables"] = variables }, "graphql", ct);
        // GraphQL reports most failures with 200 and an errors array.
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            throw GraphQLError(errors);
        return root.GetProperty("data");
    }

    /// <summary>Calls a REST endpoint (<paramref name="path"/> is relative to the API root) and returns the response JSON.</summary>
    public Task<JsonElement> RestAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct) =>
        SendAsync(method, path, body, "core", ct);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonNode? body, string resource, CancellationToken ct)
    {
        if (_exhaustedUntil.TryGetValue(resource, out var reset) && reset > DateTimeOffset.UtcNow)
            throw RateLimited(reset);

        // Tokens can come from running gh, which is slow the first time.
        var token = await Task.Run(() => credentials.GetToken(host), ct)
            ?? throw new HostException(HostErrorKind.NotAuthenticated, SignInMessage);

        using var request = new HttpRequestMessage(method, _apiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("TotalGit");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        string text;
        try
        {
            response = await http.SendAsync(request, ct);
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new HostException(HostErrorKind.Network, $"Couldn't reach GitHub: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HostException(HostErrorKind.Network, "GitHub didn't respond in time.", ex);
        }

        using (response)
        {
            var (remaining, resetAt) = TrackRateLimit(response, resource);
            if (!response.IsSuccessStatusCode) throw HttpError(response, text, remaining, resetAt);
            if (text.Length == 0) return default;
            try
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new HostException(HostErrorKind.Other, "GitHub sent a response Total Git couldn't read.", ex);
            }
        }
    }

    private (int? Remaining, DateTimeOffset? Reset) TrackRateLimit(HttpResponseMessage response, string resource)
    {
        var remaining = int.TryParse(Header(response, "X-RateLimit-Remaining"), out var r) ? r : (int?)null;
        var reset = long.TryParse(Header(response, "X-RateLimit-Reset"), out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : (DateTimeOffset?)null;
        var key = Header(response, "X-RateLimit-Resource") ?? resource;
        if (remaining == 0 && reset is { } until) _exhaustedUntil[key] = until;
        else if (remaining > 0) _exhaustedUntil.TryRemove(key, out _);
        return (remaining, reset);
    }

    private static HostException HttpError(HttpResponseMessage response, string text, int? remaining, DateTimeOffset? reset)
    {
        var message = ErrorMessage(text);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                return new(HostErrorKind.NotAuthenticated, "GitHub didn't accept the token. " + SignInMessage);
            case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests:
                if (Header(response, "X-GitHub-SSO") is not null || IsSso(message)) return new(HostErrorKind.SsoRequired, SsoMessage);
                if (remaining == 0 || response.StatusCode == HttpStatusCode.TooManyRequests || message?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) == true)
                    return RateLimited(reset ?? RetryAfter(response));
                return new(HostErrorKind.Other, message is null ? "GitHub refused the request." : $"GitHub refused the request: {message}");
            case HttpStatusCode.NotFound:
                return new(HostErrorKind.NotFound, "GitHub couldn't find that. The repository or pull request may not exist, or the token can't see it.");
            default:
                return new(HostErrorKind.Other, message is null ? $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}." : $"GitHub: {message}");
        }
    }

    private static HostException GraphQLError(JsonElement errors)
    {
        var first = errors[0];
        var message = first.Str("message") ?? "GitHub reported an error.";
        foreach (var e in errors.EnumerateArray())
        {
            if (IsSso(e.Str("message"))) return new(HostErrorKind.SsoRequired, SsoMessage);
            switch (e.Str("type"))
            {
                case "NOT_FOUND": return new(HostErrorKind.NotFound, $"GitHub: {e.Str("message")}");
                case "RATE_LIMITED": return RateLimited(null);
            }
        }
        return new(HostErrorKind.Other, $"GitHub: {message}");
    }

    private static bool IsSso(string? message) =>
        message is not null && (message.Contains("SAML", StringComparison.OrdinalIgnoreCase) || message.Contains("single sign-on", StringComparison.OrdinalIgnoreCase));

    private static HostException RateLimited(DateTimeOffset? reset) => new(HostErrorKind.RateLimited, reset is { } at
        ? $"GitHub's API rate limit is used up until {at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}."
        : "GitHub's API rate limit is used up. Try again in a few minutes.");

    private static DateTimeOffset? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { } delta ? DateTimeOffset.UtcNow + delta : response.Headers.RetryAfter?.Date;

    private static string? ErrorMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var message = doc.RootElement.Str("message");
            // 422s explain themselves in errors[]: strings, or objects with a message.
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var details = errors.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.Str("message"))
                    .OfType<string>().ToList();
                if (details.Count > 0) message = $"{message} ({string.Join("; ", details)})";
            }
            return message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
