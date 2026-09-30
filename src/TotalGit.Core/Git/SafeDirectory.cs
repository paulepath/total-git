using System.Text.RegularExpressions;

namespace TotalGit.Core.Git;

/// <summary>
/// Git's "dubious ownership" check: a repository whose folder belongs to another Windows/Unix account (another
/// drive, cloned as admin, copied from another PC) won't open until the folder is listed in the user's global
/// <c>safe.directory</c> setting. libgit2 (used for reading) and the git CLI both honour that setting.
/// </summary>
public static partial class SafeDirectory
{
    // libgit2: "repository path 'E:/x' is not owned by current user"; git: "detected dubious ownership in repository at 'E:/x'".
    [GeneratedRegex(@"repository path '(?<path>[^']+)' is not owned by current user|dubious ownership in repository at '(?<path>[^']+)'",
        RegexOptions.IgnoreCase)]
    private static partial Regex OwnershipError();

    /// <summary>The folder git refused to open because of its owner, or null for any other error.</summary>
    public static string? UntrustedPath(string? errorMessage) =>
        errorMessage is not null && OwnershipError().Match(errorMessage) is { Success: true } m ? m.Groups["path"].Value : null;

    /// <summary>
    /// Adds <paramref name="path"/> to the global <c>safe.directory</c> list (once). Git compares these entries
    /// as text, with forward slashes, so the path is written that way.
    /// </summary>
    /// <param name="globalConfigFile">A different global config file; tests use this to leave the real one alone.</param>
    public static async Task TrustAsync(string path, string? globalConfigFile = null)
    {
        var entry = path.Replace('\\', '/').TrimEnd('/');
        var env = globalConfigFile is null ? null : new Dictionary<string, string> { ["GIT_CONFIG_GLOBAL"] = globalConfigFile };
        // Run outside any repository: the setting is global, and the repository itself can't be opened yet.
        var cwd = Path.GetTempPath();

        var existing = await GitCli.RunAsync(cwd, ["config", "--global", "--get-all", "safe.directory"], throwOnError: false, env: env);
        if (existing.StdOut.Split('\n', StringSplitOptions.TrimEntries).Contains(entry, StringComparer.OrdinalIgnoreCase)) return;

        await GitCli.RunAsync(cwd, ["config", "--global", "--add", "safe.directory", entry], env: env);
    }
}
