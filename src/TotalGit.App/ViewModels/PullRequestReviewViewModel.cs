using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Git;
using TotalGit.Core.Hosting;

namespace TotalGit.App.ViewModels;

/// <summary>What the review window needs from the window it's shown in.</summary>
public interface IReviewDialogs
{
    Task<bool> ShowFormAsync(FormSpec spec);
}

/// <summary>What the review window needs from the repository tab that opened it.</summary>
public sealed record ReviewContext(
    RepositorySession Session,
    string WorkingDirectory,
    ReviewMarks Marks,
    AppSettings Settings,
    Func<PullRequestSummary, Task> Checkout,
    Func<string, Task> OpenTicket);

/// <summary>
/// A pull request in its own review window: an overview, the changed files with how far each has been reviewed, one
/// file open at a time (with the lines changed since it was reviewed marked), comments and the review itself.
/// Also reviews changes that haven't gone to a pull request (<see cref="BranchReviewSource"/>,
/// <see cref="CommitsReviewSource"/>): the same files and review marks, without the host's parts.
/// </summary>
public sealed partial class PullRequestReviewViewModel : ObservableObject
{
    private readonly ReviewContext _ctx;
    private readonly ReviewSource _source;
    private readonly string _marksKey;
    private readonly Dictionary<string, FileViewState> _viewed = [];
    private readonly Dictionary<string, ReviewThreadViewModel> _threadViewModels = [];
    private PullRequestDetails? _threadViewModelsFor;
    private IReadOnlySet<string> _sinceChanged = new HashSet<string>();
    private int _loadRequest;
    private int _diffRequest;

    public PullRequestReviewViewModel(ReviewSource source, ReviewContext ctx)
    {
        _ctx = ctx;
        _source = source;
        _marksKey = source.MarksKey;
        DiffMode = Enum.TryParse<DiffViewMode>(ctx.Settings.DiffMode, out var mode) ? mode : DiffViewMode.Inline;
        WholeFileDiff = ctx.Settings.DiffWholeFile;
        ShowAsTree = ctx.Settings.ChangedFilesTree;
        HideTests = ctx.Settings.ReviewHideTests;
        Pr = source is PullRequestSource pr ? NewPullRequest(pr.Summary, pr.Provider) : new PullRequestViewModel(LocalSummary(source), false, null);
    }

    /// <summary>The host, for a pull request; null for a local review.</summary>
    private IPullRequestProvider? Provider => (_source as PullRequestSource)?.Provider;

    /// <summary>Reviewing a pull request (comments, checks, viewed marks on the host), not local changes.</summary>
    public bool IsPullRequest => _source is PullRequestSource;
    public bool IsLocal => !IsPullRequest;

    /// <summary>"#12" before a pull request's title; nothing for a local review.</summary>
    public string NumberText => IsPullRequest ? Pr.NumberText + " " : "";

    /// <summary>Which review this is (a window per review).</summary>
    public string ReviewKey => _marksKey;

    /// <summary>A stand-in for the pull request a local review doesn't have (it has no host capabilities).</summary>
    private static PullRequestSummary LocalSummary(ReviewSource source)
    {
        var (title, head, @base) = source switch
        {
            BranchReviewSource b => ($"{b.HeadRef} against {b.BaseRef}", b.HeadRef, b.BaseRef),
            CommitsReviewSource c => (c.Count == 1 ? "1 commit" : $"{c.Count} commits", Short(c.NewestSha), Short(c.OldestSha) + "^"),
            _ => ("Review", "", ""),
        };
        return new PullRequestSummary(0, title, new PrUser("", null), false, PullRequestState.Open, @base, head, "", false,
            DateTimeOffset.Now, ChecksState.None, ReviewDecision.None, false, "");
    }

    private static string Short(string sha) => sha[..Math.Min(7, sha.Length)];

    public IReviewDialogs? Dialogs { get; set; }

    public PullRequestViewModel Pr { get; private set; }

    public PullRequestSummary Summary => Pr.Summary;
    public int Number => Summary.Number;
    public string WindowTitle => IsPullRequest ? $"#{Number} {Summary.Title} · Review" : $"{Summary.Title} · Review";
    public string BranchesText => _source switch
    {
        CommitsReviewSource c => $"{Short(c.OldestSha)} … {Short(c.NewestSha)}",
        _ => $"{Summary.HeadRef} → {Summary.BaseRef}",
    };
    public string AuthorText => IsPullRequest
        ? $"{Summary.Author.Login} wants to merge into {Summary.BaseRef}"
        : CommitItems.Count == 0 ? "" : "By " + string.Join(", ", CommitItems.GroupBy(c => c.Author).OrderByDescending(g => g.Count()).Select(g => g.Key));
    public string? Ticket { get; init; }
    public bool HasTicket => Ticket is not null;
    public bool CanMarkViewed => Provider?.Capabilities.HasFlag(PrCapabilities.ViewedFiles) == true;

    /// <summary>The window closes itself when this is raised (e.g. the repository tab was closed).</summary>
    public event Action? CloseRequested;

    public void Close() => CloseRequested?.Invoke();

    private PullRequestViewModel NewPullRequest(PullRequestSummary summary, IPullRequestProvider provider)
    {
        PullRequestViewModel? created = null;
        var vm = created = new PullRequestViewModel(summary, false, null)
        {
            Capabilities = provider.Capabilities,
            Reply = (thread, body) => PostAsync("Replying…", () => provider.ReplyAsync(summary.Number, thread, body)),
            SetResolved = (thread, resolved) => PostAsync(resolved ? "Resolving…" : "Unresolving…", () => provider.SetResolvedAsync(thread, resolved)),
            PostConversationComment = body => PostAsync("Commenting…", () => provider.AddCommentAsync(summary.Number, body)),
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PullRequestViewModel.Details) or nameof(PullRequestViewModel.Composer)) RebuildDiffThreads();
            if (e.PropertyName == nameof(PullRequestViewModel.Details)) OnDetailsChanged();
        };
        vm.PendingCommentsChanged += () =>
        {
            RebuildDiffThreads();
            OnPropertyChanged(nameof(ReviewButtonText));
        };
        return vm;
    }

    // ------------------------------------------------------------------ status

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; set; }

    public bool HasMessage => Message is not null;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? BusyText { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    private void ShowError(string message)
    {
        Message = message;
        MessageIsError = true;
    }

    private void ShowInfo(string message)
    {
        Message = message;
        MessageIsError = false;
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    // ------------------------------------------------------------------ loading

    /// <summary>Loads (or reloads) the pull request: its details from the host and its files from a local diff.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        var request = ++_loadRequest;
        IsLoading = true;
        var details = IsPullRequest ? LoadDetailsAsync(request) : Task.CompletedTask;
        try
        {
            var (mergeBase, head, files) = IsPullRequest ? await FetchFilesAsync() : await LocalFilesAsync();
            if (request != _loadRequest) return;
            var generated = await GitActions.GeneratedAttrAsync(_ctx.WorkingDirectory, files.Select(f => f.Path));
            Pr.SetFiles(files, mergeBase, head);
            _allFiles = files;
            _generated = generated;
            var commits = await GitActions.CommitListAsync(_ctx.WorkingDirectory, $"{mergeBase}..{head}");
            CommitItems = commits.Select(c => new ReviewCommitItem(c.Sha, c.Summary, c.Author)).ToList();
            Commits = CommitItems.Select(c => c.Summary).ToList();
            // A reload shows all the changes again (the commit picked may be gone after a rebase).
            _selectedCommit = null;
            OnPropertyChanged(nameof(SelectedCommit));
            OnPropertyChanged(nameof(IsCommitView));
            SetFiles(files, generated);
        }
        catch (Exception ex) when (ex is GitCommandException or LibGit2Sharp.LibGit2SharpException or IOException or InvalidOperationException)
        {
            if (request == _loadRequest) ShowError(IsPullRequest ? $"Couldn't get the pull request's changes: {ex.Message}" : $"Couldn't get the changes: {ex.Message}");
        }
        await details;
        if (request != _loadRequest) return;
        IsLoading = false;
        UpdateStates();
        await PrepareSinceLastReviewAsync();
        if (SelectedFile is { } open) await ReloadDiffAsync(open);
    }

    private async Task LoadDetailsAsync(int request)
    {
        try
        {
            var details = await Task.Run(() => Provider!.GetPullRequestAsync(Number));
            if (request == _loadRequest) Pr.Details = details;
        }
        catch (HostException ex)
        {
            if (request == _loadRequest) ShowError(ex.Message);
        }
    }

    /// <summary>Fetches the head (and the base branch), then lists the files changed since the merge-base.</summary>
    private async Task<(string MergeBase, string Head, IReadOnlyList<FileChange> Files)> FetchFilesAsync()
    {
        var provider = Provider!;
        var remote = provider.Host.RemoteName;
        var local = PullRequestRef(Number);
        var refspecs = new List<string> { $"+{provider.HeadRefSpec(Number)}:{local}", $"+refs/heads/{Summary.BaseRef}:refs/remotes/{remote}/{Summary.BaseRef}" };
        if (!Summary.IsCrossRepository) refspecs.Add($"+refs/heads/{Summary.HeadRef}:refs/remotes/{remote}/{Summary.HeadRef}");
        await GitActions.FetchRefsAsync(_ctx.WorkingDirectory, remote, [.. refspecs]);

        var session = _ctx.Session;
        return await Task.Run(() =>
        {
            var head = session.ResolveCommit(local) ?? throw new InvalidOperationException($"{local} wasn't fetched.");
            var mergeBase = session.MergeBase(head, $"refs/remotes/{remote}/{Summary.BaseRef}")
                ?? throw new InvalidOperationException($"{Summary.HeadRef} has no history in common with {Summary.BaseRef}.");
            return (mergeBase, head, session.GetRangeChanges(mergeBase, head));
        });
    }

    public static string PullRequestRef(int number) => $"refs/totalgit/pr/{number}";

    /// <summary>A local review's range, from what's here (nothing to fetch): a branch since it split from its base, or a run of commits.</summary>
    private Task<(string MergeBase, string Head, IReadOnlyList<FileChange> Files)> LocalFilesAsync()
    {
        var session = _ctx.Session;
        var source = _source;
        return Task.Run<(string, string, IReadOnlyList<FileChange>)>(() =>
        {
            switch (source)
            {
                case BranchReviewSource b:
                {
                    var head = session.ResolveCommit(b.HeadRef) ?? throw new InvalidOperationException($"{b.HeadRef} doesn't exist any more.");
                    var @base = session.MergeBase(head, b.BaseRef)
                        ?? throw new InvalidOperationException($"{b.HeadRef} has no history in common with {b.BaseRef}.");
                    return (@base, head, session.GetRangeChanges(@base, head));
                }
                case CommitsReviewSource c:
                {
                    var head = session.ResolveCommit(c.NewestSha) ?? throw new InvalidOperationException($"Commit {Short(c.NewestSha)} isn't here any more.");
                    var @base = session.ResolveCommit(c.OldestSha + "^")
                        ?? throw new InvalidOperationException($"Commit {Short(c.OldestSha)} has no parent to compare with.");
                    return (@base, head, session.GetRangeChanges(@base, head));
                }
                default:
                    throw new InvalidOperationException("Nothing to review.");
            }
        });
    }

    // ------------------------------------------------------------------ one commit at a time

    private IReadOnlyList<FileChange> _allFiles = [];
    private IReadOnlySet<string> _generated = new HashSet<string>();
    private ReviewCommitItem? _selectedCommit;

    /// <summary>The commits being reviewed, newest first; picking one shows only its changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthorText), nameof(HasSeveralCommits))]
    public partial IReadOnlyList<ReviewCommitItem> CommitItems { get; private set; } = [];

    public bool HasSeveralCommits => CommitItems.Count > 1;

    /// <summary>The one commit shown, or null for all the changes together.</summary>
    public ReviewCommitItem? SelectedCommit
    {
        get => _selectedCommit;
        set
        {
            if (_selectedCommit == value) return;
            _selectedCommit = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCommitView));
            OnPropertyChanged(nameof(CommitViewText));
            foreach (var c in CommitItems) c.IsSelected = c == value;
            _ = ShowCommitAsync(value);
        }
    }

    /// <summary>Showing one commit's changes: review boxes are read-only (they're for the whole review).</summary>
    public bool IsCommitView => _selectedCommit is not null;

    public string CommitViewText => _selectedCommit is { } c ? $"Showing only {c.Summary}" : "";

    [RelayCommand]
    private void ShowCommit(ReviewCommitItem? commit) => SelectedCommit = commit == _selectedCommit ? null : commit;

    [RelayCommand]
    private void ShowAllCommits() => SelectedCommit = null;

    /// <summary>The range diffs come from: one commit against its parent, else the whole review.</summary>
    private (string From, string To)? _commitRange;

    private async Task ShowCommitAsync(ReviewCommitItem? commit)
    {
        if (commit is null)
        {
            _commitRange = null;
            SetFiles(_allFiles, _generated);
            UpdateStates();
            return;
        }
        var session = _ctx.Session;
        try
        {
            var (from, files) = await Task.Run(() =>
            {
                var parent = session.ResolveCommit(commit.Sha + "^") ?? throw new InvalidOperationException("The first commit has no parent to compare with.");
                return (parent, session.GetRangeChanges(parent, commit.Sha));
            });
            if (_selectedCommit != commit) return;
            _commitRange = (from, commit.Sha);
            if (SinceLastReview) SinceLastReview = false;
            SetFiles(files, _generated);
            UpdateStates();
            if (SelectedFile is { } open) await ReloadDiffAsync(open);
        }
        catch (Exception ex) when (ex is LibGit2Sharp.LibGit2SharpException or ArgumentException or InvalidOperationException)
        {
            ShowError($"Couldn't show {commit.Summary}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ files and review state

    /// <summary>Every changed file; <see cref="VisibleFiles"/> is what the file list shows.</summary>
    public ObservableCollection<ReviewFileItem> Files { get; } = [];

    /// <summary>The file list: without reviewed files when hiding them, only files changed since the last review in that mode.</summary>
    public IReadOnlyList<ReviewFileItem> VisibleFiles => Files
        .Where(f => !HideReviewed || !f.IsReviewed || f.IsOpen)
        .Where(f => !HideTests || !f.IsTest || f.IsOpen)
        .Where(f => !SinceLastReview || _sinceChanged.Contains(f.Path))
        .ToList();

    public IReadOnlyList<ReviewFileItem> SourceFiles => Files.Where(f => !f.IsGenerated).ToList();
    public IReadOnlyList<ReviewFileItem> GeneratedFiles => Files.Where(f => f.IsGenerated).ToList();
    public bool HasGeneratedFiles => Files.Any(f => f.IsGenerated);
    public string GeneratedHeader => $"{GeneratedFiles.Count} generated file{(GeneratedFiles.Count == 1 ? "" : "s")} (lock files, generated code, build output)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleFiles))]
    public partial bool HideReviewed { get; set; }

    partial void OnHideReviewedChanged(bool value) => UpdateNodes();

    /// <summary>Leave test files out of the list (and out of "next file") to review them later.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleFiles), nameof(HideTestsText))]
    public partial bool HideTests { get; set; }

    partial void OnHideTestsChanged(bool value)
    {
        if (_ctx.Settings.ReviewHideTests != value)
        {
            _ctx.Settings.ReviewHideTests = value;
            _ctx.Settings.Save();
        }
        UpdateNodes();
    }

    public int TestCount => Files.Count(f => f.IsTest);
    public bool HasTests => TestCount > 0;

    /// <summary>"Hide tests (3)", and how many of them are still to review once hidden.</summary>
    public string HideTestsText
    {
        get
        {
            var left = Files.Count(f => f.IsTest && !f.IsReviewed);
            return HideTests && left > 0 ? $"Hide tests ({TestCount}, {left} to review later)" : $"Hide tests ({TestCount})";
        }
    }

    /// <summary>The file list grouped by folder (the same preference as the other changed-file lists).</summary>
    [ObservableProperty]
    public partial bool ShowAsTree { get; set; }

    partial void OnShowAsTreeChanged(bool value)
    {
        if (_ctx.Settings.ChangedFilesTree != value)
        {
            _ctx.Settings.ChangedFilesTree = value;
            _ctx.Settings.Save();
        }
        UpdateNodes(force: true);
    }

    /// <summary>The rows of the file list: folders and files, or just files.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ReviewTreeNode> FileNodes { get; private set; } = [];

    /// <summary>The selected row; picking a file's row opens the file.</summary>
    [ObservableProperty]
    public partial ReviewTreeNode? SelectedNode { get; set; }

    partial void OnSelectedNodeChanged(ReviewTreeNode? value)
    {
        if (value?.File is { } file && file != SelectedFile) SelectedFile = file;
    }

    private List<ReviewFileItem> _nodesFor = [];

    /// <summary>Rebuilds the rows when the files shown change; otherwise just updates the folders' counts.</summary>
    private void UpdateNodes(bool force = false)
    {
        var visible = VisibleFiles.ToList();
        if (!force && visible.SequenceEqual(_nodesFor))
        {
            foreach (var n in FileNodes) n.RefreshCounts();
            return;
        }
        _nodesFor = visible;
        FileNodes = ReviewTreeNode.Build(visible, ShowAsTree);
        SelectedNode = SelectedFile is { } f ? ReviewTreeNode.Find(FileNodes, f) : null;
    }

    public int ReviewedCount => Files.Count(f => f.IsReviewed);
    public string ProgressText => $"{ReviewedCount}/{Files.Count} reviewed";
    public double ProgressValue => Files.Count == 0 ? 0 : 100.0 * ReviewedCount / Files.Count;

    [ObservableProperty]
    public partial IReadOnlyList<string> Commits { get; private set; } = [];

    public string CommitsHeader => Commits.Count == 1 ? "1 commit" : $"{Commits.Count} commits";

    partial void OnCommitsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(CommitsHeader));

    private void SetFiles(IReadOnlyList<FileChange> files, IReadOnlySet<string> generated)
    {
        var open = SelectedFile?.Path;
        Files.Clear();
        foreach (var f in files) Files.Add(new ReviewFileItem(f, GeneratedFiles_IsGenerated(f.Path, generated)));
        var largest = Math.Max(1, Files.Where(f => !f.IsGenerated).Select(f => f.Size).DefaultIfEmpty(1).Max());
        foreach (var f in Files) f.SizeBarWidth = Math.Max(f.Size > 0 ? 2 : 0, 120.0 * Math.Min(f.Size, largest) / largest);
        UpdateThreadCounts();
        FilesChanged();
        // Keep the open file open after a refresh.
        if (open is not null) SelectedFile = Files.FirstOrDefault(f => f.Path == open);
    }

    private static bool GeneratedFiles_IsGenerated(string path, IReadOnlySet<string> generated) => Core.Git.GeneratedFiles.IsGenerated(path, generated);

    private void FilesChanged()
    {
        OnPropertyChanged(nameof(VisibleFiles));
        UpdateNodes();
        OnPropertyChanged(nameof(SourceFiles));
        OnPropertyChanged(nameof(GeneratedFiles));
        OnPropertyChanged(nameof(HasGeneratedFiles));
        OnPropertyChanged(nameof(GeneratedHeader));
        OnPropertyChanged(nameof(ReviewedCount));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(TestCount));
        OnPropertyChanged(nameof(HasTests));
        OnPropertyChanged(nameof(HideTestsText));
    }

    private void OnDetailsChanged()
    {
        if (Pr.Details is { } d)
        {
            _viewed.Clear();
            foreach (var (path, state) in d.ViewedFiles) _viewed[path] = state;
        }
        UpdateThreadCounts();
        UpdateStates();
        OnPropertyChanged(nameof(ReviewButtonText));
    }

    private void UpdateThreadCounts()
    {
        var threads = Pr.Details?.Threads ?? [];
        foreach (var f in Files) f.ThreadCount = threads.Count(t => !t.IsResolved && t.Anchor.Path == f.Path);
    }

    /// <summary>Each file's state, from the host's viewed mark and the local one (which knows the content reviewed).</summary>
    private void UpdateStates()
    {
        if (Pr.HeadSha is not { } head) return;
        var marks = _ctx.Marks.All(_marksKey);
        foreach (var f in Files)
        {
            var blob = f.Change.Kind == ChangeKind.Deleted ? null : _ctx.Session.BlobAt(head, f.Path);
            f.State = ReviewMarks.StateOf(_viewed.GetValueOrDefault(f.Path), marks.GetValueOrDefault(f.Path), blob);
        }
        FilesChanged();
        UpdateFileHeader();
    }

    /// <summary>Marks a file reviewed (or not), on the host and locally; puts it back if the host refuses.</summary>
    [RelayCommand]
    private async Task ToggleReviewedAsync(ReviewFileItem? file)
    {
        if (file is null || Pr.HeadSha is not { } head) return;
        if (IsCommitView)
        {
            ShowInfo("Review boxes are for all the changes: choose All changes to tick files.");
            return;
        }
        var reviewed = !file.IsReviewed;
        await SetReviewedAsync(file, reviewed, head);
    }

    private async Task<bool> SetReviewedAsync(ReviewFileItem file, bool reviewed, string head)
    {
        var before = (file.State, Viewed: _viewed.GetValueOrDefault(file.Path), Mark: _ctx.Marks.Get(_marksKey, file.Path));
        file.State = reviewed ? FileReviewState.Reviewed : FileReviewState.NotReviewed;
        _viewed[file.Path] = reviewed ? FileViewState.Viewed : FileViewState.Unviewed;
        if (reviewed)
        {
            var blob = file.Change.Kind == ChangeKind.Deleted ? null : _ctx.Session.BlobAt(head, file.Path);
            _ctx.Marks.Mark(_marksKey, file.Path, new ReviewMark(head, blob, DateTimeOffset.Now));
            // Keep the reviewed head even after force-pushes, so the changes since can be shown later.
            _ = KeepReviewedHeadAsync(file.Path, head);
        }
        else
        {
            _ctx.Marks.Unmark(_marksKey, file.Path);
        }
        FilesChanged();
        if (file == SelectedFile) await ReloadDiffAsync(file);

        if (!CanMarkViewed) return true;
        try
        {
            await Task.Run(() => Provider!.SetFileViewedAsync(Number, file.Path, reviewed));
            return true;
        }
        catch (HostException ex)
        {
            file.State = before.State;
            _viewed[file.Path] = before.Viewed;
            if (before.Mark is { } mark) _ctx.Marks.Mark(_marksKey, file.Path, mark);
            else _ctx.Marks.Unmark(_marksKey, file.Path);
            FilesChanged();
            ShowError($"Couldn't mark {file.FileName} as {(reviewed ? "viewed" : "not viewed")}: {ex.Message}");
            return false;
        }
    }

    private async Task KeepReviewedHeadAsync(string path, string head)
    {
        var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(path)))[..12];
        try
        {
            await GitActions.UpdateRefAsync(_ctx.WorkingDirectory, $"refs/totalgit/pr/{_source.RefName()}-reviewed/{hash}", head);
        }
        catch (GitCommandException)
        {
            // Only a convenience: without it the changes since review may not be shown after a force-push.
        }
    }

    /// <summary>R: marks the open file reviewed and opens the next one that isn't.</summary>
    [RelayCommand]
    private async Task MarkReviewedAndNextAsync()
    {
        if (SelectedFile is not { } file || Pr.HeadSha is not { } head) return;
        if (IsCommitView)
        {
            StepFile(1);
            return;
        }
        if (!file.IsReviewed && !await SetReviewedAsync(file, true, head)) return;
        var list = VisibleFiles.Where(f => !f.IsGenerated).ToList();
        var start = list.IndexOf(file);
        var next = list.Skip(start + 1).Concat(list.Take(Math.Max(0, start))).FirstOrDefault(f => !f.IsReviewed);
        if (next is not null) SelectedFile = next;
        else
        {
            SelectedFile = null;
            ShowInfo("Every file is reviewed.");
        }
    }

    [RelayCommand]
    private void NextFile() => StepFile(1);

    [RelayCommand]
    private void PreviousFile() => StepFile(-1);

    private void StepFile(int direction)
    {
        var list = VisibleFiles;
        if (list.Count == 0) return;
        var index = SelectedFile is { } f ? list.ToList().IndexOf(f) : -1;
        var next = index < 0 ? (direction > 0 ? 0 : list.Count - 1) : Math.Clamp(index + direction, 0, list.Count - 1);
        SelectedFile = list[next];
    }

    // ------------------------------------------------------------------ the open file

    /// <summary>The one file open, or null for the overview.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(HasOpenFile), nameof(OpenFileTitle))]
    public partial ReviewFileItem? SelectedFile { get; set; }

    public bool IsOverview => SelectedFile is null;
    public bool HasOpenFile => SelectedFile is not null;
    public string OpenFileTitle => SelectedFile?.FileName ?? "";

    partial void OnSelectedFileChanged(ReviewFileItem? oldValue, ReviewFileItem? newValue)
    {
        if (oldValue is not null) oldValue.IsOpen = false;
        if (newValue is not null) newValue.IsOpen = true;
        if (SelectedNode?.File != newValue) SelectedNode = newValue is null ? null : ReviewTreeNode.Find(FileNodes, newValue);
        ShowGenerated = false;
        Pr.Composer = null;
        _ = newValue is null ? ClearDiffAsync() : ReloadDiffAsync(newValue);
    }

    [RelayCommand]
    private void ShowOverview() => SelectedFile = null;

    [RelayCommand]
    private void OpenFile(ReviewFileItem? file) => SelectedFile = file;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiff))]
    public partial FileDiff? Diff { get; private set; }

    public bool HasDiff => Diff is not null;

    /// <summary>Where the open file changed since it was reviewed (shown in its own colour).</summary>
    [ObservableProperty]
    public partial ReviewDelta? SinceReview { get; private set; }

    /// <summary>A generated file is shown only when asked for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneratedHidden))]
    public partial bool ShowGenerated { get; set; }

    public bool IsGeneratedHidden => SelectedFile is { IsGenerated: true } && !ShowGenerated;

    [RelayCommand]
    private Task ShowGeneratedFile()
    {
        ShowGenerated = true;
        return SelectedFile is { } f ? ReloadDiffAsync(f) : Task.CompletedTask;
    }

    /// <summary>A line about the open file: its review state and what changed since the review.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileNote))]
    public partial string? FileNote { get; private set; }

    public bool HasFileNote => FileNote is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInlineDiff), nameof(IsSplitDiff))]
    public partial DiffViewMode DiffMode { get; set; }

    public bool IsInlineDiff => DiffMode == DiffViewMode.Inline;
    public bool IsSplitDiff => DiffMode == DiffViewMode.Split;

    partial void OnDiffModeChanged(DiffViewMode value)
    {
        _ctx.Settings.DiffMode = value.ToString();
        _ctx.Settings.Save();
    }

    [RelayCommand]
    private void SetDiffMode(DiffViewMode mode) => DiffMode = mode;

    [ObservableProperty]
    public partial bool WholeFileDiff { get; set; }

    partial void OnWholeFileDiffChanged(bool value)
    {
        _ctx.Settings.DiffWholeFile = value;
        _ctx.Settings.Save();
        if (SelectedFile is { } f) _ = ReloadDiffAsync(f);
    }

    private Task ClearDiffAsync()
    {
        ++_diffRequest;
        Diff = null;
        SinceReview = null;
        FileNote = null;
        return Task.CompletedTask;
    }

    private async Task ReloadDiffAsync(ReviewFileItem file)
    {
        var request = ++_diffRequest;
        OnPropertyChanged(nameof(IsGeneratedHidden));
        if (Pr.MergeBase is not { } mergeBase || Pr.HeadSha is not { } head) return;
        if (file.IsGenerated && !ShowGenerated)
        {
            Diff = null;
            SinceReview = null;
            UpdateFileHeader();
            return;
        }
        var from = SinceLastReview && LastReviewSha is { } since ? since : mergeBase;
        if (_commitRange is { } range) (from, head) = range;
        var whole = WholeFileDiff;
        var session = _ctx.Session;
        try
        {
            var mark = _ctx.Marks.Get(_marksKey, file.Path);
            var (diff, delta) = await Task.Run(() =>
            {
                var d = session.GetRangeFileDiff(from, head, file.Path, whole);
                // The lines changed since the file was reviewed, when the reviewed commit is here to compare with.
                ReviewDelta? changed = null;
                if (file.IsChanged && mark is not null && mark.HeadSha != head && !IsCommitView && session.ResolveCommit(mark.HeadSha) is not null)
                    changed = ReviewDelta.From(session.GetRangeFileDiff(mark.HeadSha, head, file.Path));
                return (d, changed);
            });
            if (request != _diffRequest) return;
            Diff = diff;
            SinceReview = delta;
            UpdateFileHeader();
            RebuildDiffThreads();
        }
        catch (Exception ex) when (ex is LibGit2Sharp.LibGit2SharpException or ArgumentException or IOException)
        {
            if (request == _diffRequest) ShowError($"Couldn't show {file.FileName}: {ex.Message}");
        }
    }

    private void UpdateFileHeader()
    {
        if (SelectedFile is not { } file)
        {
            FileNote = null;
            return;
        }
        FileNote = file.State switch
        {
            FileReviewState.ChangedSinceReview when SinceReview is { Count: > 0 } d =>
                $"Changed since you reviewed it: {d.Count} change{(d.Count == 1 ? "" : "s")} marked in violet.",
            _ when IsCommitView => null,
            FileReviewState.ChangedSinceReview when _ctx.Marks.Get(_marksKey, file.Path) is null =>
                "Changed since you marked it viewed on GitHub. Total Git can't show which lines, because it was marked elsewhere.",
            FileReviewState.ChangedSinceReview => "Changed since you reviewed it.",
            FileReviewState.Reviewed => "Reviewed.",
            _ => null,
        };
    }

    // ------------------------------------------------------------------ since the last review

    /// <summary>The head the signed-in user last reviewed at (their last review, else their oldest reviewed mark).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowSinceLastReview), nameof(SinceLastReviewText))]
    public partial string? LastReviewSha { get; private set; }

    private DateTimeOffset? _lastReviewAt;

    public bool CanShowSinceLastReview => LastReviewSha is not null && LastReviewSha != Pr.HeadSha;

    public string SinceLastReviewText => LastReviewSha is { } sha
        ? $"Since your last review ({sha[..Math.Min(7, sha.Length)]}{(_lastReviewAt is { } at ? ", " + DateText.Relative(at) : "")}): " +
          (_sinceChanged.Count == 0 ? "nothing has changed." : $"{_sinceChanged.Count} file{(_sinceChanged.Count == 1 ? "" : "s")} changed.")
        : "";

    /// <summary>Show only what changed since the last review: its files, diffed from the reviewed head.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleFiles))]
    public partial bool SinceLastReview { get; set; }

    partial void OnSinceLastReviewChanged(bool value)
    {
        UpdateNodes();
        if (SelectedFile is { } f)
        {
            if (value && !_sinceChanged.Contains(f.Path)) SelectedFile = null;
            else _ = ReloadDiffAsync(f);
        }
        // Comments need line numbers against the whole pull request.
        RebuildDiffThreads();
    }

    private async Task PrepareSinceLastReviewAsync()
    {
        if (Pr.HeadSha is not { } head) return;
        var marks = _ctx.Marks.All(_marksKey).Values.OrderBy(m => m.MarkedAt).ToList();
        var sha = Pr.Details?.LastViewerReviewSha ?? marks.FirstOrDefault()?.HeadSha;
        _lastReviewAt = Pr.Details?.LastViewerReviewSha is not null ? Pr.Details.LastViewerReviewAt : marks.FirstOrDefault()?.MarkedAt;
        if (sha is null || sha == head)
        {
            LastReviewSha = null;
            return;
        }
        // The reviewed head may only be on the host (e.g. reviewed on the web): fetch it.
        if (!await GitActions.HasCommitAsync(_ctx.WorkingDirectory, sha))
        {
            if (Provider is not { } provider)
            {
                LastReviewSha = null;
                return;
            }
            try
            {
                await GitActions.FetchRefsAsync(_ctx.WorkingDirectory, provider.Host.RemoteName, sha);
            }
            catch (GitCommandException)
            {
                LastReviewSha = null;
                return;
            }
        }
        try
        {
            var changed = await Task.Run(() => _ctx.Session.GetRangeChanges(sha, head));
            var inPr = Files.Select(f => f.Path).ToHashSet();
            _sinceChanged = changed.Select(c => c.Path).Where(inPr.Contains).ToHashSet();
        }
        catch (Exception ex) when (ex is LibGit2Sharp.LibGit2SharpException or ArgumentException)
        {
            LastReviewSha = null;
            return;
        }
        LastReviewSha = sha;
        OnPropertyChanged(nameof(SinceLastReviewText));
    }

    [RelayCommand]
    private void ShowSinceLastReview() => SinceLastReview = true;

    // ------------------------------------------------------------------ comments and the review

    [ObservableProperty]
    public partial IReadOnlyList<DiffThreadItem> DiffThreads { get; private set; } = [];

    [ObservableProperty]
    public partial bool CanCommentOnDiff { get; private set; }

    public string ReviewButtonText => Pr.ReviewButtonText;

    private void RebuildDiffThreads()
    {
        if (SelectedFile is not { } file || Diff is not { } diff || diff.Path != file.Path)
        {
            DiffThreads = [];
            CanCommentOnDiff = false;
            return;
        }
        // In "since the last review" the old side isn't the pull request's base, so comments would land wrongly.
        CanCommentOnDiff = Pr.CanComment && Pr.HeadSha is not null && !diff.IsBinary && !SinceLastReview;
        if (_threadViewModelsFor != Pr.Details)
        {
            _threadViewModels.Clear();
            _threadViewModelsFor = Pr.Details;
        }
        var items = new List<DiffThreadItem>();
        var placement = ThreadAnchoring.Place(diff.Lines, Pr.Details?.Threads ?? [], file.Path);
        foreach (var (line, threads) in placement.ByLine)
        {
            foreach (var thread in threads)
            {
                if (!_threadViewModels.TryGetValue(thread.Id, out var threadVm)) _threadViewModels[thread.Id] = threadVm = Pr.NewThread(thread);
                items.Add(new DiffThreadItem(line, threadVm));
            }
        }
        foreach (var pending in Pr.Pending.Where(p => p.Draft.Anchor.Path == file.Path))
        {
            if (LineIndexOf(diff, pending.Draft.Anchor) is { } line) items.Add(new DiffThreadItem(line, pending));
        }
        if (Pr.Composer is { } composer && composer.Anchor.Path == file.Path) items.Add(new DiffThreadItem(composer.LineIndex, composer));
        DiffThreads = items.OrderBy(i => i.LineIndex).ToList();
    }

    private static int? LineIndexOf(FileDiff diff, CommentAnchor anchor)
    {
        for (var i = 0; i < diff.Lines.Count; i++)
        {
            var line = diff.Lines[i];
            if (anchor.Side == DiffSide.Right ? line.Kind != DiffLineKind.Removed && line.NewLine == anchor.Line
                    : line.Kind != DiffLineKind.Added && line.OldLine == anchor.Line)
                return i;
        }
        return null;
    }

    /// <summary>The "+" on a diff line (or C): starts writing a comment there.</summary>
    public void StartComment(int lineIndex)
    {
        if (!CanCommentOnDiff || SelectedFile is not { } file || Diff is not { } diff || lineIndex < 0 || lineIndex >= diff.Lines.Count) return;
        if (CommentComposerViewModel.AnchorFor(file.Path, diff.Lines[lineIndex]) is not { } anchor) return;
        if (Pr.Composer is { Text.Length: > 0 } && Pr.Composer.LineIndex == lineIndex) return;
        var pr = Pr;
        pr.Composer = new CommentComposerViewModel(anchor, lineIndex, pr.Pending.Count,
            postNow: PostLineCommentAsync,
            addToReview: c =>
            {
                pr.AddPending(new DraftComment(c.Anchor, c.Text.Trim()));
                pr.Composer = null;
            },
            cancel: _ => pr.Composer = null);
    }

    private async Task<bool> PostLineCommentAsync(CommentComposerViewModel composer)
    {
        if (Pr.HeadSha is not { } head) return false;
        if (Provider is not { } provider) return false;
        var ok = await PostAsync("Commenting…", () => provider.AddLineCommentAsync(Number, composer.Anchor, composer.Text.Trim(), head));
        if (ok && Pr.Composer == composer) Pr.Composer = null;
        return ok;
    }

    /// <summary>Sends something to the host, then reloads the details (threads, conversation, reviews).</summary>
    private async Task<bool> PostAsync(string busyText, Func<Task> action)
    {
        IsBusy = true;
        BusyText = busyText;
        try
        {
            await Task.Run(action);
        }
        catch (Exception ex) when (ex is HostException or ArgumentException)
        {
            ShowError(ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
        if (Provider is not { } provider) return true;
        try
        {
            Pr.Details = await Task.Run(() => provider.GetPullRequestAsync(Number));
        }
        catch (HostException ex)
        {
            ShowError(ex.Message);
        }
        return true;
    }

    /// <summary>Submits a review: a summary, the pending line comments, and comment / approve / request changes.</summary>
    [RelayCommand]
    private async Task SubmitReviewAsync()
    {
        var pr = Pr;
        if (Dialogs is null || pr.HeadSha is not { } head || Provider is not { } provider) return;
        (ReviewVerdict Verdict, FormChoice Choice)[] verdicts = pr.ViewerIsAuthor
            ? [(ReviewVerdict.Comment, new FormChoice("Comment", "Send your comments. Hosts don't let you approve or request changes on your own pull request."))]
            :
            [
                (ReviewVerdict.Comment, new FormChoice("Comment", "Send your comments without approving.")),
                (ReviewVerdict.Approve, new FormChoice("Approve", "Approve these changes for merging.")),
                (ReviewVerdict.RequestChanges, new FormChoice("Request changes", "Changes are needed before this can be merged.", IsWarning: true)),
            ];
        var summary = new FormField(FormFieldKind.MultilineText, "Summary") { Placeholder = "Leave a comment (optional)" };
        var verdict = FormField.Choice("Review", verdicts.Select(v => v.Choice).ToList());
        var unreviewed = Files.Count(f => !f.IsReviewed && !f.IsGenerated);
        var pendingText = (pr.Pending.Count switch
        {
            0 => "No line comments are waiting.",
            1 => "1 line comment will be sent with the review.",
            var n => $"{n} line comments will be sent with the review.",
        }) + (unreviewed > 0 ? $" {unreviewed} file{(unreviewed == 1 ? " isn't" : "s aren't")} marked reviewed yet." : "");
        var spec = new FormSpec($"Review #{pr.Number}", pendingText, "Submit review", [summary, verdict], Validate: () =>
            verdicts[verdict.SelectedIndex].Verdict != ReviewVerdict.Approve && summary.Text.Trim().Length == 0 && pr.Pending.Count == 0
                ? "Write a summary or add line comments."
                : null);
        if (!await Dialogs.ShowFormAsync(spec)) return;

        var drafts = pr.Pending.Select(p => p.Draft).ToList();
        var choice = verdicts[verdict.SelectedIndex].Verdict;
        if (await PostAsync("Submitting review…", () => provider.SubmitReviewAsync(pr.Number, choice, summary.Text.Trim(), drafts, head)))
        {
            pr.ClearPending();
            ShowInfo(choice switch
            {
                ReviewVerdict.Approve => $"Approved #{pr.Number}.",
                ReviewVerdict.RequestChanges => $"Requested changes on #{pr.Number}.",
                _ => $"Review sent on #{pr.Number}.",
            });
        }
    }

    // ------------------------------------------------------------------ header actions

    [RelayCommand]
    private void OpenInBrowser()
    {
        if (IsPullRequest) UrlLauncher.Open(Summary.Url);
    }

    [RelayCommand]
    private Task Checkout() => IsPullRequest ? _ctx.Checkout(Summary) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenTicket() => Ticket is { } t ? _ctx.OpenTicket(t) : Task.CompletedTask;

    [RelayCommand]
    private static void OpenUrl(string? url)
    {
        if (url is not null) UrlLauncher.Open(url);
    }

    [RelayCommand]
    private void OpenThread(ReviewThreadViewModel thread)
    {
        if (Files.FirstOrDefault(f => f.Path == thread.Thread.Anchor.Path) is { } file) SelectedFile = file;
    }
}
