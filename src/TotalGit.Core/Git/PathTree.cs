namespace TotalGit.Core.Git;

/// <summary>A folder or a file in a <see cref="PathTree"/>.</summary>
/// <param name="Name">Display name: the file name, or the folder name (several segments when compacted).</param>
/// <param name="FolderPath">A folder's full path with a trailing slash; null for files.</param>
public sealed record PathTreeNode<T>(string Name, string? FolderPath, T? Item, IReadOnlyList<PathTreeNode<T>> Children, int FileCount)
{
    public bool IsFolder => FolderPath is not null;
}

/// <summary>Groups repository paths into a folder tree.</summary>
public static class PathTree
{
    /// <summary>
    /// Builds the tree: folders first, then files, each sorted by name. A folder whose only content is one
    /// subfolder is merged with it (<c>infra/local-env</c>), as Git Extensions does.
    /// </summary>
    public static IReadOnlyList<PathTreeNode<T>> Build<T>(IEnumerable<T> items, Func<T, string> pathOf)
    {
        var root = new Folder<T>("");
        foreach (var item in items)
        {
            var parts = pathOf(item).Split('/');
            var folder = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!folder.Folders.TryGetValue(parts[i], out var child))
                    folder.Folders[parts[i]] = child = new Folder<T>(folder.Path + parts[i] + "/");
                folder = child;
            }
            folder.Files.Add((parts[^1], item));
        }
        return Convert(root);
    }

    private static List<PathTreeNode<T>> Convert<T>(Folder<T> folder)
    {
        var result = new List<PathTreeNode<T>>();
        foreach (var (name, sub) in folder.Folders.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            var label = name;
            var node = sub;
            while (node.Files.Count == 0 && node.Folders.Count == 1)
            {
                var (childName, child) = node.Folders.First();
                label += "/" + childName;
                node = child;
            }
            var children = Convert(node);
            result.Add(new PathTreeNode<T>(label, node.Path, default, children, children.Sum(c => c.FileCount)));
        }
        foreach (var (name, item) in folder.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            result.Add(new PathTreeNode<T>(name, null, item, [], 1));
        return result;
    }

    private sealed class Folder<T>(string path)
    {
        public string Path { get; } = path;
        public Dictionary<string, Folder<T>> Folders { get; } = new(StringComparer.Ordinal);
        public List<(string Name, T Item)> Files { get; } = [];
    }
}
