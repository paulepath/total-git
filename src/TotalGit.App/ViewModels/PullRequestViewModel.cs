using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.Core.Git;
using TotalGit.Core.Hosting;

namespace TotalGit.App.ViewModels;

/// <summary>Colours for pull request states (checks, reviews, merge state).</summary>
public static class PrColors
{
    public static readonly IBrush Success = new SolidColorBrush(Color.Parse("#4CC38A"));
    public static readonly IBrush Failure = new SolidColorBrush(Color.Parse("#E5484D"));
    public static readonly IBrush Pending = new SolidColorBrush(Color.Parse("#E8B339"));
    public static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#8A9099"));
}

/// <summary>
/// One pull request in the right-hand panel: its header (state, checks, reviewers), changed files, checks and
/// conversation. The summary shows at once; details come from the host and the files from a local diff between
/// the merge-base and the pull request's head.
/// </summary>
public sealed partial class PullRequestViewModel : ObservableObject
{
    private readonly bool _showAsTree;
    private readonly Action<bool>? _showAsTreeChanged;

    public PullRequestViewModel(PullRequestSummary summary, bool showAsTree, Action<bool>? showAsTreeChanged)
    {
        Summary = summary;
        _showAsTree = showAsTree;
        _showAsTreeChanged = showAsTreeChanged;
    }

    public PullRequestSummary Summary { get; }
    public int Number => Summary.Number;
    public string NumberText => $"#{Summary.Number}";
    public string Title => Summary.Title;
    public string AuthorText => $"{Summary.Author.Login} wants to merge {Summary.HeadRef} into {Summary.BaseRef}";
    public bool IsDraft => Summary.IsDraft;

    public ICommand? OpenInBrowserCommand { get; init; }
    public ICommand? CheckoutCommand { get; init; }
    public ICommand? RefreshCommand { get; init; }
    public ICommand? ReviewCommand { get; init; }

    /// <summary>What the host lets this app do with pull requests.</summary>
    public PrCapabilities Capabilities { get; init; }

    public Func<ReviewThread, string, Task<bool>>? Reply { get; init; }
    public Func<ReviewThread, bool, Task<bool>>? SetResolved { get; init; }
    public Func<string, Task<bool>>? PostConversationComment { get; init; }

    public bool CanComment => Capabilities.HasFlag(PrCapabilities.LineComments);
    public bool CanReview => Capabilities.HasFlag(PrCapabilities.Reviews);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoadingFiles))]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    // ------------------------------------------------------------------ files

    /// <summary>The commit the files are compared from (where the pull request branched off its base).</summary>
    public string? MergeBase { get; private set; }

    /// <summary>The pull request's head commit, fetched locally.</summary>
    public string? HeadSha { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFiles), nameof(FilesTabText), nameof(IsLoadingFiles))]
    public partial FileTreeViewModel? FileTree { get; private set; }

    public bool HasFiles => FileTree is not null;
    public bool IsLoadingFiles => FileTree is null && IsLoading;
    public string FilesTabText => FileTree is null ? "Files" : $"Files ({FileTree.Files.Count})";

    public void SetFiles(IReadOnlyList<FileChange> files, string mergeBase, string headSha)
    {
        MergeBase = mergeBase;
        HeadSha = headSha;
        var tree = new FileTreeViewModel(files.Select(f => new FileChangeItem(f, false)).ToArray(), _showAsTree, _showAsTreeChanged);
        tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileTreeViewModel.SelectedFile)) OnPropertyChanged(nameof(SelectedFile));
        };
        FileTree = tree;
    }

    public FileChangeItem? SelectedFile
    {
        get => FileTree?.SelectedFile;
        set
        {
            if (FileTree is not null) FileTree.SelectedFile = value;
        }
    }

    // ------------------------------------------------------------------ details from the host

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails), nameof(Body), nameof(HasBody), nameof(StatsText), nameof(MergeText),
        nameof(MergeBrush), nameof(Reviewers), nameof(HasReviewers), nameof(Checks), nameof(HasChecks), nameof(ChecksTabText),
        nameof(Conversation), nameof(ConversationTabText), nameof(ViewerIsAuthor))]
    public partial PullRequestDetails? Details { get; set; }

    public bool HasDetails => Details is not null;
    public string? Body => Details?.Body is { Length: > 0 } b ? b.Trim() : null;
    public bool HasBody => Body is not null;

    public string? StatsText => Details is { } d
        ? $"{d.CommitCount} commit{(d.CommitCount == 1 ? "" : "s")} · {d.ChangedFiles} file{(d.ChangedFiles == 1 ? "" : "s")} · +{d.Additions} −{d.Deletions}"
        : null;

    public IReadOnlyList<ReviewerItem> Reviewers => Details?.Reviewers.Select(r => new ReviewerItem(r)).ToList() ?? [];
    public bool HasReviewers => Reviewers.Count > 0;

    public IReadOnlyList<CheckItemViewModel> Checks => Details?.Checks
        .OrderBy(c => c.Status switch { CheckStatus.Failure => 0, CheckStatus.Pending => 1, _ => 2 })
        .ThenByDescending(c => c.IsRequired)
        .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
        .Select(c => new CheckItemViewModel(c)).ToList() ?? [];
    public bool HasChecks => Checks.Count > 0;

    public string ChecksTabText
    {
        get
        {
            if (Details is not { } d || d.Checks.Count == 0) return "Checks";
            var failed = d.Checks.Count(c => c.Status == CheckStatus.Failure);
            return failed > 0 ? $"Checks ({failed} failed)" : $"Checks ({d.Checks.Count})";
        }
    }

    /// <summary>The description first, then comments and reviews, oldest first.</summary>
    public IReadOnlyList<ConversationItem> Conversation => Details is { } d
        ? d.Timeline.Where(t => t.Body.Trim().Length > 0 || t.ReviewState is ReviewState.Approved or ReviewState.ChangesRequested)
            .Select(t => new ConversationItem(t)).ToList()
        : [];

    public string ConversationTabText => Conversation.Count + FileThreads.Count is var n && n == 0 ? "Conversation" : $"Conversation ({n})";

    /// <summary>Whether it can be merged, and what's in the way: shown as a banner under the title.</summary>
    public string? MergeText
    {
        get
        {
            if (Details is not { } d) return null;
            if (d.Summary.IsDraft) return "Draft: not ready for review yet.";
            var reasons = new List<string>();
            var failing = d.Checks.Count(c => c.IsRequired && c.Status == CheckStatus.Failure);
            var pending = d.Checks.Count(c => c.IsRequired && c.Status == CheckStatus.Pending);
            if (failing > 0) reasons.Add($"{failing} required check{(failing == 1 ? "" : "s")} failing");
            if (pending > 0) reasons.Add($"{pending} required check{(pending == 1 ? "" : "s")} running");
            if (d.Summary.ReviewDecision == ReviewDecision.ChangesRequested) reasons.Add("changes requested");
            if (d.Summary.ReviewDecision == ReviewDecision.ReviewRequired) reasons.Add("approval required");
            return d.MergeState switch
            {
                MergeState.Conflicting => $"Has conflicts with {d.Summary.BaseRef} that must be resolved.",
                MergeState.Behind => $"Behind {d.Summary.BaseRef}: the branch must be updated first.",
                MergeState.Blocked when reasons.Count > 0 => "Blocked: " + string.Join(", ", reasons) + ".",
                MergeState.Blocked => "Blocked by the branch's rules.",
                MergeState.Unstable => "Can be merged, but some checks failed.",
                MergeState.Clean => "Ready to merge.",
                _ when reasons.Count > 0 => string.Join(", ", reasons) + ".",
                _ => null,
            };
        }
    }

    public IBrush MergeBrush => Details?.MergeState switch
    {
        MergeState.Clean => PrColors.Success,
        MergeState.Conflicting or MergeState.Blocked => PrColors.Failure,
        MergeState.Behind or MergeState.Unstable => PrColors.Pending,
        _ => PrColors.Neutral,
    };

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsLoadingFiles));

    partial void OnDetailsChanged(PullRequestDetails? value)
    {
        FileThreads = value?.Threads
            .OrderBy(t => t.Anchor.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Anchor.Line ?? t.Anchor.OriginalLine ?? 0)
            .Select(NewThread).ToList() ?? [];
    }

    // ------------------------------------------------------------------ comments and reviews

    public ReviewThreadViewModel NewThread(ReviewThread thread) => new(thread,
        Reply ?? ((_, _) => Task.FromResult(false)),
        SetResolved ?? ((_, _) => Task.FromResult(false)));

    /// <summary>All line comment threads, by file: shown on the Conversation tab (including outdated ones).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileThreads), nameof(FileThreadsHeader), nameof(ConversationTabText))]
    public partial IReadOnlyList<ReviewThreadViewModel> FileThreads { get; private set; } = [];

    public bool HasFileThreads => FileThreads.Count > 0;

    public string FileThreadsHeader
    {
        get
        {
            var open = FileThreads.Count(t => !t.IsResolved);
            return $"Comments on files ({open} open, {FileThreads.Count - open} resolved)";
        }
    }

    /// <summary>Opens the file a thread is on.</summary>
    [RelayCommand]
    private void OpenThread(ReviewThreadViewModel thread)
    {
        if (FileTree?.Files.FirstOrDefault(f => f.Path == thread.Thread.Anchor.Path) is { } file) SelectedFile = file;
    }

    /// <summary>The new line comment being written in the diff, if any.</summary>
    [ObservableProperty]
    public partial CommentComposerViewModel? Composer { get; set; }

    /// <summary>Line comments waiting to be sent with the review.</summary>
    public ObservableCollection<PendingCommentViewModel> Pending { get; } = [];

    public bool HasPending => Pending.Count > 0;
    public string ReviewButtonText => Pending.Count == 0 ? "Review…" : $"Finish review ({Pending.Count})";

    public void AddPending(DraftComment draft)
    {
        Pending.Add(new PendingCommentViewModel(draft, p => RemovePending(p)));
        PendingChanged();
    }

    public void RemovePending(PendingCommentViewModel pending)
    {
        Pending.Remove(pending);
        PendingChanged();
    }

    public void ClearPending()
    {
        Pending.Clear();
        PendingChanged();
    }

    /// <summary>Raised when the pending comments change, so the diff can show them.</summary>
    public event Action? PendingCommentsChanged;

    private void PendingChanged()
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(ReviewButtonText));
        PendingCommentsChanged?.Invoke();
    }

    /// <summary>You opened it: hosts don't let authors approve or request changes on their own pull requests.</summary>
    public bool ViewerIsAuthor => Details?.ViewerIsAuthor == true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendConversationCommentCommand))]
    public partial string ConversationText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendConversationCommentCommand))]
    public partial bool IsSending { get; set; }

    private bool CanSendConversationComment() => !IsSending && ConversationText.Trim().Length > 0 && PostConversationComment is not null;

    [RelayCommand(CanExecute = nameof(CanSendConversationComment))]
    private async Task SendConversationCommentAsync()
    {
        IsSending = true;
        try
        {
            if (await PostConversationComment!(ConversationText.Trim())) ConversationText = "";
        }
        finally
        {
            IsSending = false;
        }
    }
}

public sealed class ReviewerItem(Reviewer reviewer)
{
    public string Name => reviewer.Name;

    public string StateText => reviewer.State switch
    {
        ReviewState.Approved => "approved",
        ReviewState.ChangesRequested => "requested changes",
        ReviewState.Commented => "commented",
        ReviewState.Dismissed => "dismissed",
        _ => reviewer.IsRequested ? "review requested" : "",
    };

    public IBrush StateBrush => reviewer.State switch
    {
        ReviewState.Approved => PrColors.Success,
        ReviewState.ChangesRequested => PrColors.Failure,
        _ => PrColors.Neutral,
    };
}

public sealed class CheckItemViewModel(CheckItem check)
{
    public CheckItem Check => check;
    public string Name => check.Name;
    public string? Description => check.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(check.Description);
    public bool IsRequired => check.IsRequired;
    public string? DetailsUrl => check.DetailsUrl;
    public bool HasDetailsUrl => DetailsUrl is not null;

    public string StatusText => check.Status switch
    {
        CheckStatus.Success => "passed",
        CheckStatus.Failure => "failed",
        CheckStatus.Pending => "running",
        CheckStatus.Skipped => "skipped",
        CheckStatus.Cancelled => "cancelled",
        _ => "neutral",
    };

    public IBrush StatusBrush => check.Status switch
    {
        CheckStatus.Success => PrColors.Success,
        CheckStatus.Failure => PrColors.Failure,
        CheckStatus.Pending => PrColors.Pending,
        _ => PrColors.Neutral,
    };
}

public sealed class ConversationItem(TimelineItem item)
{
    public string Author => item.Author.Login;
    public string Body => item.Body.Trim();
    public bool HasBody => Body.Length > 0;
    public string When => item.CreatedAt.LocalDateTime.ToString("g");

    public string? Verdict => item.ReviewState switch
    {
        ReviewState.Approved => "approved",
        ReviewState.ChangesRequested => "requested changes",
        _ when item.Kind == TimelineKind.Review => "reviewed",
        _ => null,
    };
    public bool HasVerdict => Verdict is not null;

    public IBrush VerdictBrush => item.ReviewState switch
    {
        ReviewState.Approved => PrColors.Success,
        ReviewState.ChangesRequested => PrColors.Failure,
        _ => PrColors.Neutral,
    };
}
