using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TotalGit.Core.Git;
using TotalGit.Core.Hosting;

namespace TotalGit.App.ViewModels;

/// <summary>One changed file in the pull request review window: the change, how far it's been reviewed, and size.</summary>
public sealed partial class ReviewFileItem(FileChange change, bool isGenerated) : ObservableObject
{
    private static readonly IBrush ReviewedBrush = new SolidColorBrush(Color.Parse("#4CC38A"));
    private static readonly IBrush ChangedBrush = new SolidColorBrush(Color.Parse("#E8B339"));
    private static readonly IBrush NotReviewedBrush = new SolidColorBrush(Color.Parse("#6B727C"));

    public FileChange Change { get; } = change;
    public FileChangeItem Item { get; } = new(change, false);
    public string Path => Change.Path;
    public string FileName => Change.DisplayName;
    public string? Folder => Change.Directory is { Length: > 0 } d ? d : null;
    public bool HasFolder => Folder is not null;
    public string ToolTip => Change.OldPath is { } old ? $"{old} → {Change.Path}" : Change.Path;
    public string StatusLetter => Item.StatusLetter;
    public IBrush StatusBrush => Item.StatusBrush;
    public string AddedText => Change.Additions > 0 ? $"+{Change.Additions}" : "";
    public string DeletedText => Change.Deletions > 0 ? $"−{Change.Deletions}" : "";
    public int Size => Change.Additions + Change.Deletions;

    /// <summary>Lock files, generated code and build output: folded unless asked for.</summary>
    public bool IsGenerated { get; } = isGenerated;

    /// <summary>Test code (by its name or folder): can be hidden to review later.</summary>
    public bool IsTest { get; } = TestFiles.IsTest(change.Path);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReviewed), nameof(IsChanged), nameof(IsNotReviewed), nameof(StateBrush), nameof(StateText))]
    public partial FileReviewState State { get; set; }

    public bool IsReviewed => State == FileReviewState.Reviewed;
    public bool IsChanged => State == FileReviewState.ChangedSinceReview;
    public bool IsNotReviewed => State == FileReviewState.NotReviewed;

    public IBrush StateBrush => State switch
    {
        FileReviewState.Reviewed => ReviewedBrush,
        FileReviewState.ChangedSinceReview => ChangedBrush,
        _ => NotReviewedBrush,
    };

    public string StateText => State switch
    {
        FileReviewState.Reviewed => "Reviewed. Click to mark as not reviewed.",
        FileReviewState.ChangedSinceReview => "Changed since you reviewed it. Click to mark it reviewed again.",
        _ => "Not reviewed yet. Click to mark it reviewed.",
    };

    /// <summary>Open comment threads on the file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThreads), nameof(ThreadText))]
    public partial int ThreadCount { get; set; }

    public bool HasThreads => ThreadCount > 0;
    public string ThreadText => ThreadCount.ToString();

    /// <summary>Width of the size bar on the overview, in pixels (set once the largest file is known).</summary>
    [ObservableProperty]
    public partial double SizeBarWidth { get; set; }

    /// <summary>The added share of the size bar.</summary>
    public double AddedBarWidth => Size == 0 ? 0 : SizeBarWidth * Change.Additions / Size;
    public double DeletedBarWidth => SizeBarWidth - AddedBarWidth;

    partial void OnSizeBarWidthChanged(double value)
    {
        OnPropertyChanged(nameof(AddedBarWidth));
        OnPropertyChanged(nameof(DeletedBarWidth));
    }

    /// <summary>The file is open in the window.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }
}
