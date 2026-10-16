using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TotalGit.VisualStudio.Git;

/// <summary>One entry of <c>git worktree list --porcelain</c>.</summary>
internal sealed class Worktree
{
    /// <summary>The folder as a native path.</summary>
    public string Path { get; set; } = "";
    public string? Head { get; set; }
    /// <summary>The short branch name; null when detached or bare.</summary>
    public string? Branch { get; set; }
    public bool Bare { get; set; }
    public bool Detached { get; set; }
    public bool Locked { get; set; }
    /// <summary>git would prune it: its folder is gone.</summary>
    public bool Prunable { get; set; }
    /// <summary>The first git lists: the repository's main working folder.</summary>
    public bool IsMain { get; set; }
}

/// <summary>What <c>git status --porcelain=v2 --branch</c> says about a worktree.</summary>
internal sealed class WorktreeStatus
{
    public string? Upstream { get; set; }
    public int Ahead { get; set; }
    public int Behind { get; set; }
    /// <summary>Changed, staged, conflicted and untracked entries.</summary>
    public int Changes { get; set; }
    /// <summary>Conflicted files, relative to the worktree with forward slashes.</summary>
    public List<string> Conflicts { get; } = new();
}

internal static class Porcelain
{
    public static List<Worktree> ParseWorktreeList(string output)
    {
        var result = new List<Worktree>();
        Worktree? current = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                current = new Worktree
                {
                    Path = System.IO.Path.GetFullPath(line.Substring("worktree ".Length)),
                    IsMain = result.Count == 0,
                };
                result.Add(current);
                continue;
            }
            if (current is null) continue;
            var space = line.IndexOf(' ');
            var key = space < 0 ? line : line.Substring(0, space);
            var value = space < 0 ? "" : line.Substring(space + 1);
            switch (key)
            {
                case "HEAD": current.Head = value; break;
                case "branch": current.Branch = value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value.Substring(11) : value; break;
                case "detached": current.Detached = true; break;
                case "bare": current.Bare = true; break;
                case "locked": current.Locked = true; break;
                case "prunable": current.Prunable = true; break;
            }
        }
        return result;
    }

    public static WorktreeStatus ParseStatus(string output)
    {
        var status = new WorktreeStatus();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                status.Upstream = line.Substring("# branch.upstream ".Length);
            }
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ');
                if (parts.Length >= 4 && int.TryParse(parts[2].TrimStart('+'), out var ahead) && int.TryParse(parts[3].TrimStart('-'), out var behind))
                {
                    status.Ahead = ahead;
                    status.Behind = behind;
                }
            }
            else if (!line.StartsWith("#", StringComparison.Ordinal))
            {
                status.Changes++;
                // u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
                if (line.StartsWith("u ", StringComparison.Ordinal))
                {
                    var fields = line.Split(new[] { ' ' }, 11);
                    if (fields.Length == 11) status.Conflicts.Add(fields[10]);
                }
            }
        }
        return status;
    }

    /// <summary>The worktree's folder name, as Total Git names it.</summary>
    public static string Name(string folder) => System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));

    public static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    public static bool IsUnder(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string p) => p.Replace('/', '\\').TrimEnd('\\');

    /// <summary>"↑2 ↓1" (only the non-zero parts).</summary>
    public static string AheadBehind(WorktreeStatus? s)
    {
        if (s is null) return "";
        var parts = new List<string>();
        if (s.Ahead > 0) parts.Add("↑" + s.Ahead);
        if (s.Behind > 0) parts.Add("↓" + s.Behind);
        return string.Join(" ", parts);
    }

    /// <summary>Solution files in a worktree, shallowest first (as Total Git's own finder does).</summary>
    public static List<string> FindSolutions(string root, int maxDepth = 3)
    {
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", ".worktrees", "node_modules", "bin", "obj", "packages", "artifacts", "dist", "out",
        };
        var found = new List<(int Depth, string Path)>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var ext = System.IO.Path.GetExtension(file);
                    if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
                        found.Add((depth, file));
                }
                if (depth >= maxDepth) continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var info = new DirectoryInfo(sub);
                    if (skipped.Contains(info.Name) || info.Name.StartsWith(".", StringComparison.Ordinal)
                        || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Skip folders that can't be read.
            }
        }
        return found.OrderBy(f => f.Depth).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(f => f.Path).ToList();
    }
}
