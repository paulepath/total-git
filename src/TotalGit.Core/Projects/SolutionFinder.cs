namespace TotalGit.Core.Projects;

/// <summary>Finds Visual Studio solution files (.sln, .slnx) in a worktree.</summary>
public static class SolutionFinder
{
    // Build output, dependencies and other worktrees: never where a repository's own solutions live.
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".worktrees", "node_modules", "bin", "obj", "packages", "artifacts", "dist", "out",
    };

    /// <summary>
    /// Solution files under <paramref name="root"/> (up to <paramref name="maxDepth"/> folders down), shallowest
    /// first, then by path. Hidden folders, symlinked folders and build/dependency folders are skipped.
    /// </summary>
    public static IReadOnlyList<string> Find(string root, int maxDepth = 3)
    {
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
                    var ext = Path.GetExtension(file);
                    if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
                        found.Add((depth, file));
                }
                if (depth >= maxDepth) continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var info = new DirectoryInfo(sub);
                    if (SkippedFolders.Contains(info.Name) || info.Name.StartsWith('.')
                        || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Skip folders we can't read.
            }
        }
        return found
            .OrderBy(f => f.Depth)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.Path)
            .ToList();
    }
}
