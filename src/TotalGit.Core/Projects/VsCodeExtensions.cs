using System.Text.Json;
using System.Text.RegularExpressions;

namespace TotalGit.Core.Projects;

/// <summary>Which version of a VS Code extension is installed, read from VS Code's extensions folder (without running it).</summary>
public static partial class VsCodeExtensions
{
    /// <summary>The user's VS Code extensions folder: VSCODE_EXTENSIONS if set (as VS Code itself does), else ~/.vscode/extensions.</summary>
    public static string DefaultFolder() =>
        Environment.GetEnvironmentVariable("VSCODE_EXTENSIONS") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");

    /// <summary>The installed version of extension <paramref name="id"/> (publisher.name), or null if it isn't installed.</summary>
    public static Version? InstalledVersion(string folder, string id)
    {
        try
        {
            // extensions.json is VS Code's list of what's installed; an old version's folder stays until VS Code restarts.
            var list = Path.Combine(folder, "extensions.json");
            if (File.Exists(list)) return FromExtensionsJson(File.ReadAllText(list), id);
            return Directory.Exists(folder) ? FromFolderNames(Directory.EnumerateDirectories(folder).Select(Path.GetFileName)!, id) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The version of <paramref name="id"/> in VS Code's extensions.json, or null.</summary>
    public static Version? FromExtensionsJson(string json, string id)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        Version? best = null;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object
                || !e.TryGetProperty("identifier", out var identifier) || identifier.ValueKind != JsonValueKind.Object
                || !identifier.TryGetProperty("id", out var idProp)
                || !string.Equals(idProp.GetString(), id, StringComparison.OrdinalIgnoreCase)
                || !e.TryGetProperty("version", out var versionProp)
                || ParseVersion(versionProp.GetString()) is not { } version) continue;
            if (best is null || version > best) best = version;
        }
        return best;
    }

    /// <summary>The highest version among folders named <c>publisher.name-1.2.3</c> (perhaps with a platform suffix).</summary>
    public static Version? FromFolderNames(IEnumerable<string> names, string id)
    {
        Version? best = null;
        foreach (var name in names)
        {
            if (!name.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase)) continue;
            if (ParseVersion(name[(id.Length + 1)..]) is { } version && (best is null || version > best)) best = version;
        }
        return best;
    }

    /// <summary>"1.2.3" (or "1.2.3-beta", "1.2.3-win32-x64") as a three-part version; null if it isn't one.</summary>
    public static Version? ParseVersion(string? text) =>
        text is not null && VersionPattern().Match(text) is { Success: true } m
            ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
            : null;

    [GeneratedRegex(@"^(\d{1,9})\.(\d{1,9})\.(\d{1,9})(?:$|[-+])")]
    private static partial Regex VersionPattern();
}
