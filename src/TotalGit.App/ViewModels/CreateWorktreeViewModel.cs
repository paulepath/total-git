using CommunityToolkit.Mvvm.ComponentModel;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

/// <summary>State of the "Create worktree" dialog.</summary>
public sealed partial class CreateWorktreeViewModel : ObservableObject
{
    private readonly IReadOnlyCollection<string> _localBranches;
    private bool _nameEdited;

    public CreateWorktreeViewModel(
        string mainRoot,
        WorktreeSource source,
        string branch,
        string? startPoint,
        string startPointLabel,
        IReadOnlyCollection<string> localBranches,
        IReadOnlyList<string> patterns,
        bool copyLocalFiles,
        bool linkNodeModules)
    {
        MainRoot = mainRoot;
        Source = source;
        StartPoint = startPoint;
        StartPointLabel = startPointLabel;
        _localBranches = localBranches;
        SetName("");
        Branch = branch; // also suggests Name
        Patterns = string.Join(Environment.NewLine, patterns);
        CopyLocalFiles = copyLocalFiles;
        LinkNodeModules = linkNodeModules;
    }

    public string MainRoot { get; }
    public WorktreeSource Source { get; }
    public string? StartPoint { get; }
    public string StartPointLabel { get; }

    public string Title => Source switch
    {
        WorktreeSource.LocalBranch => "Create worktree for branch",
        WorktreeSource.RemoteBranch => "Create worktree from remote branch",
        _ => "Create worktree with new branch",
    };

    public string SourceDescription => Source switch
    {
        WorktreeSource.LocalBranch => $"Checks out the existing branch '{Branch}'.",
        WorktreeSource.RemoteBranch => $"Creates a local branch tracking {StartPoint}.",
        _ => $"Creates a new branch starting at {StartPointLabel}.",
    };

    public bool CanEditBranch => Source != WorktreeSource.LocalBranch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(IsValid))]
    public partial string Branch { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPath), nameof(Error), nameof(IsValid))]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial bool CopyLocalFiles { get; set; }

    [ObservableProperty]
    public partial bool LinkNodeModules { get; set; }

    [ObservableProperty]
    public partial string Patterns { get; set; }

    public string TargetPath => WorktreeService.PathFor(MainRoot, Name.Trim());

    public string? Error
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Branch)) return "Enter a branch name.";
            if (Branch.Contains(' ') || Branch.Contains("..") || Branch.EndsWith('/') || Branch.StartsWith('-')) return "That isn't a valid branch name.";
            if (Source != WorktreeSource.LocalBranch && _localBranches.Contains(Branch.Trim())) return $"A branch named '{Branch.Trim()}' already exists.";
            if (string.IsNullOrWhiteSpace(Name)) return "Enter a folder name.";
            if (Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "The folder name contains invalid characters.";
            if (Directory.Exists(TargetPath)) return $"{TargetPath} already exists.";
            return null;
        }
    }

    public bool IsValid => Error is null;

    partial void OnBranchChanged(string value)
    {
        if (!_nameEdited) SetName(WorktreeService.SuggestName(value));
    }

    partial void OnNameChanged(string value)
    {
        if (!_settingName) _nameEdited = true;
    }

    private bool _settingName;

    private void SetName(string value)
    {
        _settingName = true;
        Name = value;
        _settingName = false;
    }

    public IReadOnlyList<string> PatternList =>
        Patterns.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public WorktreeCreateRequest ToRequest() => new(
        MainRoot,
        Name.Trim(),
        Source,
        Branch.Trim(),
        StartPoint,
        CopyLocalFiles,
        PatternList,
        LinkNodeModules);
}
