using System.Net;
using System.Text;

namespace TotalGit.Core.Tests.Fakes;

/// <summary>Serves canned responses by URL prefix (longest match wins) and records every request with its body.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = [];
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Request bodies, in the same order as <see cref="Requests"/> (null when there was none).</summary>
    public List<string?> Bodies { get; } = [];

    public void Route(string urlPrefix, Func<HttpResponseMessage> response) => Routes[urlPrefix] = _ => response();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
        var url = request.RequestUri!.ToString();
        var route = Routes.Where(r => url.StartsWith(r.Key, StringComparison.Ordinal)).OrderByDescending(r => r.Key.Length).FirstOrDefault();
        return route.Value?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
}
