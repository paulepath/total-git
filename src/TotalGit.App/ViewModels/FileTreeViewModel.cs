using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.Core.Git;

namespace TotalGit.App.ViewModels;

/// <summary>
/// A read-only list of changed files (a commit's, or another worktree's) shown as a folder tree with collapsible
/// folders, or as a flat list with each file's folder beside it.
/// </summary>
public sealed partial class FileTreeViewModel : ObservableObject
{
    // Folders the user collapsed; kept when switching between tree and list.
    private readonly HashSet<string> _collapsed = [];
    private readonly Action<bool>? _showAsTreeChanged;

    public FileTreeViewModel(IReadOnlyList<FileChangeItem> files, bool showAsTree, Action<bool>? showAsTreeChanged)
    {
        Files = files;
        _showAsTree = showAsTree;
        Nodes = Build();
        _showAsTreeChanged = showAsTreeChanged;
    }

    public IReadOnlyList<FileChangeItem> Files { get; }
    public string FileCountText => Files.Count == 1 ? "1 file changed" : $"{Files.Count} files changed";

    [ObservableProperty]
    public partial IReadOnlyList<StagingNode> Nodes { get; private set; }

    [ObservableProperty]
    public partial FileChangeItem? SelectedFile { get; set; }

    /// <summary>Folder tree, or a flat list with the folder next to each file.</summary>
    public bool ShowAsTree
    {
        get => _showAsTree;
        set
        {
            if (!SetProperty(ref _showAsTree, value)) return;
            Nodes = Build();
            _showAsTreeChanged?.Invoke(value);
        }
    }
    private bool _showAsTree;

    [RelayCommand]
    private void ExpandAll() => SetAllExpanded(Nodes, true);

    [RelayCommand]
    private void CollapseAll() => SetAllExpanded(Nodes, false);

    private static void SetAllExpanded(IEnumerable<StagingNode> nodes, bool expanded)
    {
        foreach (var n in nodes.Where(n => n.IsFolder))
        {
            n.IsExpanded = expanded;
            SetAllExpanded(n.Children, expanded);
        }
    }

    private List<StagingNode> Build()
    {
        if (!ShowAsTree) return Files.Select(f => StagingNode.ForFile(f, showFolder: true)).ToList();
        return Convert(PathTree.Build(Files, f => f.Path));

        List<StagingNode> Convert(IReadOnlyList<PathTreeNode<FileChangeItem>> nodes) => nodes
            .Select(n => n.IsFolder
                ? StagingNode.ForFolder(n.Name, n.FolderPath!, n.FileCount, isStaged: false,
                    isExpanded: !_collapsed.Contains(n.FolderPath!), Convert(n.Children), OnFolderExpandedChanged)
                : StagingNode.ForFile(n.Item!, showFolder: false))
            .ToList();
    }

    private void OnFolderExpandedChanged(StagingNode folder)
    {
        if (folder.FolderPath is not { } path) return;
        if (folder.IsExpanded) _collapsed.Remove(path);
        else _collapsed.Add(path);
    }

    /// <summary>The row showing <paramref name="file"/>, or null.</summary>
    public StagingNode? NodeFor(FileChangeItem? file) => file is null ? null : Find(Nodes, file);

    private static StagingNode? Find(IEnumerable<StagingNode> nodes, FileChangeItem file)
    {
        foreach (var n in nodes)
        {
            if (n.File == file) return n;
            if (n.FolderPath is { } folder && file.Path.StartsWith(folder, StringComparison.Ordinal) && Find(n.Children, file) is { } found)
                return found;
        }
        return null;
    }
}
