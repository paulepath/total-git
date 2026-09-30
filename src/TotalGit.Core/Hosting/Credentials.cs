using System.Collections.Concurrent;
using System.Diagnostics;

namespace TotalGit.Core.Hosting;

/// <summary>Supplies an access token for a hosting service, or null when none is available.</summary>
public interface ICredentialSource
{
    /// <param name="host">The service's host name, e.g. <c>github.com</c>.</param>
    string? GetToken(string host);
}

/// <summary>A token from environment variables, for one host (e.g. GITHUB_TOKEN / GH_TOKEN for github.com).</summary>
public sealed class EnvTokenSource(string host, params string[] variables) : ICredentialSource
{
    public string? GetToken(string h)
    {
        if (!string.Equals(h, host, StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var name in variables)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) return value;
        return null;
    }
}

/// <summary>The GitHub CLI's login (<c>gh auth token</c>); null when gh isn't installed or signed in.</summary>
public sealed class GhCliTokenSource : ICredentialSource
{
    public string? GetToken(string host)
    {
        try
        {
            var psi = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in (string[])["auth", "token", "--hostname", host]) psi.ArgumentList.Add(arg);
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
}

/// <summary>
/// Asks each source in turn and remembers the answer per host (running gh is slow).
/// <see cref="Invalidate"/> forgets the answers, e.g. after the user signs in.
/// </summary>
public sealed class ChainedCredentialSource(params ICredentialSource[] sources) : ICredentialSource
{
    private readonly ConcurrentDictionary<string, Lazy<string?>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>GITHUB_TOKEN / GH_TOKEN, else the GitHub CLI's login.</summary>
    public static ChainedCredentialSource Default() =>
        new(new EnvTokenSource("github.com", "GITHUB_TOKEN", "GH_TOKEN"), new GhCliTokenSource());

    public string? GetToken(string host) =>
        _cache.GetOrAdd(host, h => new Lazy<string?>(() => sources.Select(s => s.GetToken(h)).FirstOrDefault(t => t is not null))).Value;

    public void Invalidate() => _cache.Clear();
}
