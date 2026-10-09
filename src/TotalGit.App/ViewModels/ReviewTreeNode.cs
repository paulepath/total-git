using CommunityToolkit.Mvvm.ComponentModel;

namespace TotalGit.App.ViewModels;

/// <summary>A row of the review window's file list: a folder (in the tree) or a file.</summary>
public sealed partial class ReviewTreeNode : ObservableObject
{
    private ReviewTreeNode(string name, ReviewFileItem? file, bool showFolder)
    {
        Name = name;
        File = file;
        ShowFolder = showFolder;
    }

    public string Name { get; }
    public ReviewFileItem? File { get; }
    public bool IsFolder => File is null;
    public bool IsFile => File is not null;

    /// <summary>In the flat list, each file shows its folder under its name.</summary>
    public bool ShowFolder { get; }

    public List<ReviewTreeNode> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    /// <summary>"2/3": how many files under a folder are approved or rejected (and unchanged since).</summary>
    public string ReviewedText
    {
        get
        {
            var files = AllFiles().ToList();
            return $"{files.Count(f => f.IsDone)}/{files.Count}";
        }
    }

    public IEnumerable<ReviewFileItem> AllFiles() =>
        File is not null ? [File] : Children.SelectMany(c => c.AllFiles());

    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(ReviewedText));
        foreach (var c in Children) c.RefreshCounts();
    }

    /// <summary>The files, flat (each with its folder) or grouped by folder with single-folder chains joined ("src/lib").</summary>
    public static List<ReviewTreeNode> Build(IEnumerable<ReviewFileItem> files, bool asTree)
    {
        var list = files.ToList();
        if (!asTree) return list.Select(f => new ReviewTreeNode(f.FileName, f, showFolder: true)).ToList();

        var root = new ReviewTreeNode("", null, false);
        foreach (var file in list.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var node = root;
            foreach (var part in (file.Folder ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = node.Children.FirstOrDefault(c => c.IsFolder && c.Name == part);
                if (next is null)
                {
                    next = new ReviewTreeNode(part, null, false);
                    node.Children.Add(next);
                }
                node = next;
            }
            node.Children.Add(new ReviewTreeNode(file.FileName, file, false));
        }
        return Compress(root).Children.OrderByDescending(c => c.IsFolder).ToList();
    }

    /// <summary>Joins a folder holding only one folder with it, and puts folders before files.</summary>
    private static ReviewTreeNode Compress(ReviewTreeNode node)
    {
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            if (!child.IsFolder) continue;
            while (child.Children.Count == 1 && child.Children[0].IsFolder)
            {
                var only = child.Children[0];
                var joined = new ReviewTreeNode($"{child.Name}/{only.Name}", null, false);
                joined.Children.AddRange(only.Children);
                child = joined;
            }
            node.Children[i] = Compress(child);
        }
        var ordered = node.Children.OrderByDescending(c => c.IsFolder).ToList();
        node.Children.Clear();
        node.Children.AddRange(ordered);
        return node;
    }

    /// <summary>The node for a file, searching the tree.</summary>
    public static ReviewTreeNode? Find(IEnumerable<ReviewTreeNode> nodes, ReviewFileItem file)
    {
        foreach (var n in nodes)
        {
            if (n.File == file) return n;
            if (Find(n.Children, file) is { } found) return found;
        }
        return null;
    }
}
