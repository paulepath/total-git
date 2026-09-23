using System.Text.RegularExpressions;
using TotalGit.Core.Git;

namespace TotalGit.Core.Worktrees;

public sealed record WorktreeInfo(
    string Path,
    string? Branch,
    string? HeadSha,
    bool IsMain,
    bool IsBare,
    bool IsLocked,
    bool IsPrunable,
    string? PrunableReason)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
    public bool IsDetached => Branch is null && !IsBare;
}

/// <summary>Lists worktrees and derives names following the <c>.worktrees/&lt;name&gt;</c> convention.</summary>
public static partial class WorktreeService
{
    public const string WorktreesFolder = ".worktrees";

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex InvalidNameChars();

    /// <summary>Lists all worktrees of the repository containing <paramref name="anyWorktreePath"/>; the main one is first.</summary>
    public static async Task<IReadOnlyList<WorktreeInfo>> ListAsync(string anyWorktreePath)
    {
        var output = (await GitCli.RunAsync(anyWorktreePath, "worktree", "list", "--porcelain")).StdOut;
        return ParsePorcelain(output);
    }

    public static IReadOnlyList<WorktreeInfo> ParsePorcelain(string output)
    {
        var result = new List<WorktreeInfo>();
        foreach (var block in output.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? path = null, head = null, branch = null, prunableReason = null;
            bool bare = false, locked = false, prunable = false;

            foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var (key, value) = line.IndexOf(' ') is var i and >= 0 ? (line[..i], line[(i + 1)..]) : (line, "");
                switch (key)
                {
                    case "worktree": path = NormalizePath(value); break;
                    case "HEAD": head = value; break;
                    case "branch": branch = value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value["refs/heads/".Length..] : value; break;
                    case "bare": bare = true; break;
                    case "locked": locked = true; break;
                    case "prunable": prunable = true; prunableReason = value; break;
                }
            }

            if (path is not null)
                result.Add(new WorktreeInfo(path, branch, head, result.Count == 0, bare, locked, prunable, prunableReason));
        }
        return result;
    }

    /// <summary>Folder name for a branch's worktree: the last path segment, made filesystem-safe.</summary>
    public static string SuggestName(string branch)
    {
        var last = branch.TrimEnd('/').Split('/')[^1];
        var name = InvalidNameChars().Replace(last, "-").Trim('-', '.');
        return name.Length == 0 ? "worktree" : name;
    }

    public static string PathFor(string mainWorktreeRoot, string name) =>
        Path.Combine(mainWorktreeRoot, WorktreesFolder, name);

    public static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool SamePath(string a, string b) =>
        string.Equals(NormalizePath(a), NormalizePath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
