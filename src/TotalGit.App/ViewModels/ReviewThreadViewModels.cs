using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.Core.Git;
using TotalGit.Core.Hosting;

namespace TotalGit.App.ViewModels;

/// <summary>Something shown between diff lines: a thread, a comment being written, or a pending review comment.</summary>
/// <param name="LineIndex">The index into the diff's lines it's shown below.</param>
public sealed record DiffThreadItem(int LineIndex, object ViewModel);

public sealed class CommentViewModel(PrComment comment)
{
    public string Author => comment.Author.Login;
    public string Body => comment.Body.Trim();
    public string When => comment.CreatedAt.LocalDateTime.ToString("g");
}

/// <summary>A review thread: its comments, a reply box, and resolve / unresolve.</summary>
public sealed partial class ReviewThreadViewModel : ObservableObject
{
    private readonly Func<ReviewThread, string, Task<bool>> _reply;
    private readonly Func<ReviewThread, bool, Task<bool>> _setResolved;

    public ReviewThreadViewModel(ReviewThread thread, Func<ReviewThread, string, Task<bool>> reply, Func<ReviewThread, bool, Task<bool>> setResolved)
    {
        Thread = thread;
        _reply = reply;
        _setResolved = setResolved;
        Comments = thread.Comments.Select(c => new CommentViewModel(c)).ToList();
        // Resolved conversations start folded, as on GitHub.
        IsExpanded = !thread.IsResolved;
    }

    public ReviewThread Thread { get; }
    public IReadOnlyList<CommentViewModel> Comments { get; }
    public bool IsResolved => Thread.IsResolved;
    public bool IsOutdated => Thread.IsOutdated;
    public bool CanReply => Thread.ViewerCanReply;
    public bool CanResolve => Thread.ViewerCanResolve;
    public string ResolveText => IsResolved ? "Unresolve" : "Resolve";

    /// <summary>Where the thread is, for lists outside the diff ("src/app.py:12").</summary>
    public string Location => Thread.Anchor.Line is { } line ? $"{Thread.Anchor.Path}:{line}"
        : Thread.Anchor.OriginalLine is { } original ? $"{Thread.Anchor.Path}:{original}" : Thread.Anchor.Path;

    public string Summary
    {
        get
        {
            var first = Comments.FirstOrDefault();
            var count = Comments.Count == 1 ? "1 comment" : $"{Comments.Count} comments";
            var state = IsResolved ? "Resolved · " : IsOutdated ? "Outdated · " : "";
            return first is null ? state + count : $"{state}{count} · {first.Author}: {FirstLine(first.Body)}";
        }
    }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplyCommand))]
    public partial string ReplyText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplyCommand), nameof(ToggleResolvedCommand))]
    public partial bool IsBusy { get; set; }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    private bool CanSendReply() => !IsBusy && ReplyText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSendReply))]
    private async Task ReplyAsync()
    {
        IsBusy = true;
        try
        {
            if (await _reply(Thread, ReplyText.Trim())) ReplyText = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanToggleResolved() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanToggleResolved))]
    private async Task ToggleResolvedAsync()
    {
        IsBusy = true;
        try { await _setResolved(Thread, !IsResolved); }
        finally { IsBusy = false; }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 80 ? line[..80] + "…" : line;
    }
}

/// <summary>A new line comment being written: post it now, or add it to a review to send later.</summary>
public sealed partial class CommentComposerViewModel : ObservableObject
{
    private readonly Func<CommentComposerViewModel, Task<bool>> _postNow;
    private readonly Action<CommentComposerViewModel> _addToReview;
    private readonly Action<CommentComposerViewModel> _cancel;

    public CommentComposerViewModel(CommentAnchor anchor, int lineIndex, int pendingCount,
        Func<CommentComposerViewModel, Task<bool>> postNow, Action<CommentComposerViewModel> addToReview, Action<CommentComposerViewModel> cancel)
    {
        Anchor = anchor;
        LineIndex = lineIndex;
        PendingCount = pendingCount;
        _postNow = postNow;
        _addToReview = addToReview;
        _cancel = cancel;
    }

    public CommentAnchor Anchor { get; }
    public int LineIndex { get; }
    public int PendingCount { get; }

    public string Title => $"Comment on {(Anchor.Side == DiffSide.Left ? "old " : "")}line {Anchor.Line}";

    /// <summary>"Start a review" at first, "Add to review" once comments are waiting.</summary>
    public string AddToReviewText => PendingCount == 0 ? "Start a review" : $"Add to review ({PendingCount} pending)";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PostNowCommand), nameof(AddToReviewCommand))]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PostNowCommand), nameof(AddToReviewCommand))]
    public partial bool IsBusy { get; set; }

    private bool HasText() => !IsBusy && Text.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(HasText))]
    private async Task PostNowAsync()
    {
        IsBusy = true;
        try { await _postNow(this); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(HasText))]
    private void AddToReview() => _addToReview(this);

    [RelayCommand]
    private void Cancel() => _cancel(this);

    /// <summary>
    /// The anchor for the diff line a comment is started on: the old side for a removed line, else the new side.
    /// Null for lines that can't take comments (hunk headers).
    /// </summary>
    public static CommentAnchor? AnchorFor(string path, DiffLine line) => line switch
    {
        { Kind: DiffLineKind.Removed, OldLine: { } old } => new CommentAnchor(path, DiffSide.Left, old),
        { Kind: DiffLineKind.Added or DiffLineKind.Context, NewLine: { } n } => new CommentAnchor(path, DiffSide.Right, n),
        _ => null,
    };
}

/// <summary>A line comment added to the review being written: sent when the review is submitted.</summary>
public sealed partial class PendingCommentViewModel(DraftComment draft, Action<PendingCommentViewModel> remove) : ObservableObject
{
    public DraftComment Draft { get; } = draft;
    public string Body => Draft.Body;
    public string Location => $"{Draft.Anchor.Path}:{Draft.Anchor.Line}";

    [RelayCommand]
    private void Remove() => remove(this);
}
