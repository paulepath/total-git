using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TotalGit.Core.Avatars;

/// <summary>Pure helpers for deriving avatar sources from an author's identity.</summary>
public static partial class AvatarIdentity
{
    [GeneratedRegex(@"^(?:(?<id>\d+)\+)?(?<user>[^@]+)@users\.noreply\.github\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubNoReply();

    [GeneratedRegex(@"github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubRemote();

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Avatar URL for GitHub "noreply" commit emails, or null for any other address.</summary>
    public static string? GitHubNoReplyAvatarUrl(string email)
    {
        var m = GitHubNoReply().Match(email.Trim());
        if (!m.Success) return null;
        return m.Groups["id"].Success
            ? $"https://avatars.githubusercontent.com/u/{m.Groups["id"].Value}?s=64"
            : $"https://github.com/{m.Groups["user"].Value}.png?size=64";
    }

    /// <summary>Extracts owner/repo from an https or ssh GitHub remote URL.</summary>
    public static (string Owner, string Repo)? ParseGitHubRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = GitHubRemote().Match(url.Trim());
        return m.Success ? (m.Groups["owner"].Value, m.Groups["repo"].Value) : null;
    }

    public static string GravatarUrl(string email) =>
        $"https://www.gravatar.com/avatar/{Sha256Hex(NormalizeEmail(email))}?s=64&d=404";

    public static string CacheKey(string email) => Sha256Hex(NormalizeEmail(email))[..32];

    /// <summary>One or two uppercase letters for an initials badge.</summary>
    public static string Initials(string name)
    {
        var parts = name.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => string.Concat(parts[0][..1], parts[^1][..1]).ToUpperInvariant(),
        };
    }

    /// <summary>Stable hue (0-359) for an initials badge background.</summary>
    public static int Hue(string email) =>
        BitConverter.ToUInt16(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeEmail(email))), 0) % 360;

    private static string Sha256Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
