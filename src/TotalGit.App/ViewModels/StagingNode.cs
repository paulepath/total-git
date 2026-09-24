using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TotalGit.App.ViewModels;

/// <summary>A folder or file row in the staging trees.</summary>
public sealed partial class StagingNode : ObservableObject
{
    private Action<StagingNode>? _expandedChanged;

    private StagingNode(bool isStaged) => IsStaged = isStaged;

    public static StagingNode ForFile(FileChangeItem file, bool showFolder) => new(file.IsStaged)
    {
        File = file,
        Name = file.FileName,
        Subtitle = showFolder ? file.Folder : null,
    };

    public static StagingNode ForFolder(string name, string folderPath, int fileCount, bool isStaged, bool isExpanded,
        IReadOnlyList<StagingNode> children, Action<StagingNode> expandedChanged)
    {
        var node = new StagingNode(isStaged)
        {
            Name = name,
            FolderPath = folderPath,
            FileCount = fileCount,
            Children = children,
            IsExpanded = isExpanded,
        };
        // Attached after the initial state so building the tree doesn't report changes.
        node._expandedChanged = expandedChanged;
        return node;
    }

    public bool IsStaged { get; }
    public FileChangeItem? File { get; private init; }
    public string Name { get; private init; } = "";

    /// <summary>A folder's path with a trailing slash; null for files.</summary>
    public string? FolderPath { get; private init; }
    public int FileCount { get; private init; }
    public IReadOnlyList<StagingNode> Children { get; private init; } = [];

    /// <summary>The file's folder, shown in list mode.</summary>
    public string? Subtitle { get; private init; }
    public bool HasSubtitle => Subtitle is not null;

    public bool IsFolder => File is null;
    public bool IsFile => File is not null;
    public string? StatusLetter => File?.StatusLetter;
    public IBrush? StatusBrush => File?.StatusBrush;
    public string? ToolTip => File?.ToolTip ?? FolderPath;
    public string ActionText => IsStaged ? "Unstage" : "Stage";

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value) => _expandedChanged?.Invoke(this);

    /// <summary>The files in this row (itself, or everything below the folder).</summary>
    public IEnumerable<FileChangeItem> Files() => File is { } f ? [f] : Children.SelectMany(c => c.Files());
}
