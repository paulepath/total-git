using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Git;
using TotalGit.Core.Hosting;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

// Pull requests from the repository's hosting service: the sidebar list, the panel for one pull request, its files
// (diffed locally after fetching its head), and checking it out into a worktree.
public partial class RepositoryViewModel
{
    private IPullRequestProvider? _prProvider;
    private RemoteHostInfo? _prHost;
    private IReadOnlyList<PullRequestSummary> _pullRequests = [];
    private int _prListRequest;
    private int _prRequest;
    private DateTimeOffset _prListLoadedAt;

    /// <summary>Creates pull request providers for supported hosts; set by the shell. Null hides pull requests.</summary>
    public IPullRequestProviderFactory? PullRequestProviders { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPullRequest))]
    public partial PullRequestViewModel? PullRequest { get; set; }

    public bool ShowPullRequest => PullRequest is not null;

    /// <summary>After a repository loads: picks the provider for its remote and lists its pull requests.</summary>
    private void UpdatePullRequestHost()
    {
        var host = PullRequestProviders is null ? null : RemoteHostParser.Parse(_state?.OriginUrl);
        if (host == _prHost) return;
        _prHost = host;
        _prProvider = host is null ? null : PullRequestProviders!.TryCreate(host);
        _pullRequests = [];
        PullRequest = null;
        if (_prProvider is null)
        {
            Sidebar.SetPullRequests(false, [], null);
            return;
        }
        _ = RefreshPullRequestsAsync();
    }

    /// <summary>Lists the open pull requests again (unless <paramref name="ifOlderThan"/> says the list is recent).</summary>
    public async Task RefreshPullRequestsAsync(TimeSpan? ifOlderThan = null)
    {
        if (_prProvider is not { } provider) return;
        if (ifOlderThan is { } age && DateTimeOffset.Now - _prListLoadedAt < age) return;
        var request = ++_prListRequest;
        _prListLoadedAt = DateTimeOffset.Now;
        if (_pullRequests.Count == 0) Sidebar.SetPullRequests(true, [], "Loading…");
        try
        {
            // Off the UI thread: the first call may run `gh auth token` for the token.
            var list = await Task.Run(() => provider.ListOpenAsync());
            if (request != _prListRequest || provider != _prProvider) return;
            _pullRequests = list;
            Sidebar.SetPullRequests(true, list, list.Count == 0 ? "No open pull requests" : null);
        }
        catch (HostException ex)
        {
            if (request != _prListRequest || provider != _prProvider) return;
            _prListLoadedAt = default; // try again next time
            Sidebar.SetPullRequests(true, _pullRequests, ex.Message);
        }
    }

    [RelayCommand]
    private Task RefreshPullRequestList() => RefreshPullRequestsAsync();

    /// <summary>Shows a pull request in the right-hand panel and loads its details and files.</summary>
    [RelayCommand]
    private async Task OpenPullRequestAsync(PullRequestSummary summary)
    {
        if (_prProvider is not { } provider || _state is null || _session is not { } session) return;
        var request = ++_prRequest;

        // Leaves the commit selection: the panel shows the pull request instead.
        SelectedSha = null;
        SelectedRange = null;
        PullRequestViewModel? created = null;
        var vm = created = new PullRequestViewModel(summary, Settings.ChangedFilesTree, SaveChangedFilesTree)
        {
            Capabilities = provider.Capabilities,
            OpenInBrowserCommand = new RelayCommand(() => OpenUrl(summary.Url)),
            CheckoutCommand = new AsyncRelayCommand(() => CheckoutPullRequestAsync(summary)),
            RefreshCommand = new AsyncRelayCommand(() => OpenPullRequestAsync(summary)),
            ReviewCommand = new AsyncRelayCommand(() => SubmitReviewAsync(created!)),
            Reply = (thread, body) => PostAsync(created!, "Replying…", () => provider.ReplyAsync(summary.Number, thread, body)),
            SetResolved = (thread, resolved) => PostAsync(created!, resolved ? "Resolving…" : "Unresolving…",
                () => provider.SetResolvedAsync(thread, resolved)),
            PostConversationComment = body => PostAsync(created!, "Commenting…", () => provider.AddCommentAsync(summary.Number, body)),
        };
        vm.PropertyChanged += OnChildPropertyChanged;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PullRequestViewModel.Details) or nameof(PullRequestViewModel.Composer)) RebuildDiffThreads();
        };
        vm.PendingCommentsChanged += RebuildDiffThreads;
        PullRequest = vm;

        var details = LoadPullRequestDetailsAsync(provider, vm, summary.Number, request);
        try
        {
            var (mergeBase, head, files) = await FetchPullRequestFilesAsync(provider, summary, session);
            if (request != _prRequest) return;
            vm.SetFiles(files, mergeBase, head);
        }
        catch (Exception ex) when (ex is GitCommandException or LibGit2Sharp.LibGit2SharpException or IOException or InvalidOperationException)
        {
            if (request == _prRequest) vm.Error = $"Couldn't get the pull request's changes: {ex.Message}";
        }
        await details;
        if (request == _prRequest) vm.IsLoading = false;
    }

    private async Task LoadPullRequestDetailsAsync(IPullRequestProvider provider, PullRequestViewModel vm, int number, int request)
    {
        try
        {
            var details = await Task.Run(() => provider.GetPullRequestAsync(number));
            if (request == _prRequest) vm.Details = details;
        }
        catch (HostException ex)
        {
            if (request == _prRequest) vm.Error = ex.Message;
        }
    }

    /// <summary>
    /// Fetches the pull request's head (and its base branch) into local refs, then lists the files changed since
    /// the merge-base, as the host's "Files changed" view does.
    /// </summary>
    private async Task<(string MergeBase, string Head, IReadOnlyList<FileChange> Files)> FetchPullRequestFilesAsync(
        IPullRequestProvider provider, PullRequestSummary pr, RepositorySession session)
    {
        var dir = _state!.WorkingDirectory;
        var remote = provider.Host.RemoteName;
        var local = PullRequestRef(pr.Number);
        var refspecs = new List<string> { $"+{provider.HeadRefSpec(pr.Number)}:{local}", $"+refs/heads/{pr.BaseRef}:refs/remotes/{remote}/{pr.BaseRef}" };
        // A branch on this remote: update its remote branch too, so checking it out tracks the latest.
        if (!pr.IsCrossRepository) refspecs.Add($"+refs/heads/{pr.HeadRef}:refs/remotes/{remote}/{pr.HeadRef}");
        await GitActions.FetchRefsAsync(dir, remote, [.. refspecs]);

        return await Task.Run(() =>
        {
            var head = session.ResolveCommit(local) ?? throw new InvalidOperationException($"{local} wasn't fetched.");
            var mergeBase = session.MergeBase(head, $"refs/remotes/{remote}/{pr.BaseRef}")
                ?? throw new InvalidOperationException($"{pr.HeadRef} has no history in common with {pr.BaseRef}.");
            return (mergeBase, head, session.GetRangeChanges(mergeBase, head));
        });
    }

    // ------------------------------------------------------------------ comments and reviews

    /// <summary>Threads, the comment being written and pending review comments on the open diff's lines.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DiffThreadItem> DiffThreads { get; private set; } = [];

    /// <summary>The diff shows a pull request's file, so its lines can be commented on.</summary>
    [ObservableProperty]
    public partial bool CanCommentOnDiff { get; private set; }

    // Any diff change (another file, a commit, closing it) or another pull request changes what's shown.
    partial void OnDiffChanged(FileDiff? value) => RebuildDiffThreads();

    partial void OnPullRequestChanged(PullRequestViewModel? value) => RebuildDiffThreads();

    // Thread view models are kept while the details are the same, so a half-written reply survives rebuilding.
    private readonly Dictionary<string, ReviewThreadViewModel> _threadViewModels = [];
    private PullRequestDetails? _threadViewModelsFor;

    /// <summary>Rebuilds what's shown between the diff's lines for the pull request file it shows.</summary>
    private void RebuildDiffThreads()
    {
        if (PullRequest is not { SelectedFile: { } file } pr || Diff is not { } diff || diff.Path != file.Path)
        {
            DiffThreads = [];
            CanCommentOnDiff = false;
            return;
        }
        CanCommentOnDiff = pr.CanComment && pr.HeadSha is not null && !diff.IsBinary;

        if (_threadViewModelsFor != pr.Details)
        {
            _threadViewModels.Clear();
            _threadViewModelsFor = pr.Details;
        }
        var items = new List<DiffThreadItem>();
        var placement = ThreadAnchoring.Place(diff.Lines, pr.Details?.Threads ?? [], file.Path);
        foreach (var (line, threads) in placement.ByLine)
        {
            foreach (var thread in threads)
            {
                if (!_threadViewModels.TryGetValue(thread.Id, out var threadVm)) _threadViewModels[thread.Id] = threadVm = pr.NewThread(thread);
                items.Add(new DiffThreadItem(line, threadVm));
            }
        }
        foreach (var pending in pr.Pending.Where(p => p.Draft.Anchor.Path == file.Path))
        {
            if (LineIndexOf(diff, pending.Draft.Anchor) is { } line) items.Add(new DiffThreadItem(line, pending));
        }
        if (pr.Composer is { } composer && composer.Anchor.Path == file.Path) items.Add(new DiffThreadItem(composer.LineIndex, composer));
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

    /// <summary>The "+" on a diff line: starts writing a comment there.</summary>
    public void StartComment(int lineIndex)
    {
        if (PullRequest is not { SelectedFile: { } file } pr || Diff is not { } diff || lineIndex < 0 || lineIndex >= diff.Lines.Count) return;
        if (CommentComposerViewModel.AnchorFor(file.Path, diff.Lines[lineIndex]) is not { } anchor) return;
        // Keep a comment that's being written on that line.
        if (pr.Composer is { Text.Length: > 0 } && pr.Composer.LineIndex == lineIndex) return;
        pr.Composer = new CommentComposerViewModel(anchor, lineIndex, pr.Pending.Count,
            postNow: c => PostLineCommentAsync(pr, c),
            addToReview: c =>
            {
                pr.AddPending(new DraftComment(c.Anchor, c.Text.Trim()));
                pr.Composer = null;
            },
            cancel: _ => pr.Composer = null);
    }

    private async Task<bool> PostLineCommentAsync(PullRequestViewModel pr, CommentComposerViewModel composer)
    {
        if (_prProvider is not { } provider || pr.HeadSha is not { } head) return false;
        var ok = await PostAsync(pr, "Commenting…", () => provider.AddLineCommentAsync(pr.Number, composer.Anchor, composer.Text.Trim(), head));
        if (ok && pr.Composer == composer) pr.Composer = null;
        return ok;
    }

    /// <summary>Sends something to the host, then reloads the pull request's details (threads, conversation, reviews).</summary>
    private async Task<bool> PostAsync(PullRequestViewModel pr, string busyText, Func<Task> action)
    {
        if (_prProvider is not { } provider) return false;
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
        try
        {
            var details = await Task.Run(() => provider.GetPullRequestAsync(pr.Number));
            if (PullRequest == pr) pr.Details = details;
        }
        catch (HostException ex)
        {
            ShowError(ex.Message);
        }
        return true;
    }

    /// <summary>Submits a review: a summary, the pending line comments, and comment / approve / request changes.</summary>
    private async Task SubmitReviewAsync(PullRequestViewModel pr)
    {
        if (_prProvider is not { } provider || Dialogs is null || pr.HeadSha is not { } head) return;
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
        var pendingText = pr.Pending.Count switch
        {
            0 => "No line comments are waiting.",
            1 => "1 line comment will be sent with the review.",
            var n => $"{n} line comments will be sent with the review.",
        };
        var spec = new FormSpec($"Review #{pr.Number}", pendingText, "Submit review", [summary, verdict], Validate: () =>
            verdicts[verdict.SelectedIndex].Verdict != ReviewVerdict.Approve && summary.Text.Trim().Length == 0 && pr.Pending.Count == 0
                ? "Write a summary or add line comments."
                : null);
        if (!await Dialogs.ShowFormAsync(spec)) return;

        var drafts = pr.Pending.Select(p => p.Draft).ToList();
        var choice = verdicts[verdict.SelectedIndex].Verdict;
        if (await PostAsync(pr, "Submitting review…", () => provider.SubmitReviewAsync(pr.Number, choice, summary.Text.Trim(), drafts, head)))
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

    /// <summary>Where a pull request's head is fetched to (outside refs/heads and refs/remotes, so it isn't listed as a branch).</summary>
    private static string PullRequestRef(int number) => $"refs/totalgit/pr/{number}";

    /// <summary>
    /// Checks the pull request out into a new worktree: its own branch when it's on this remote (so pushes update
    /// the pull request), otherwise a new local branch at its head.
    /// </summary>
    [RelayCommand]
    private async Task CheckoutPullRequestAsync(PullRequestSummary pr)
    {
        if (_prProvider is not { } provider || _state is null) return;
        var remote = provider.Host.RemoteName;
        var ok = await RunGitAsync($"Fetching #{pr.Number}…", () =>
        {
            var refspecs = new List<string> { $"+{provider.HeadRefSpec(pr.Number)}:{PullRequestRef(pr.Number)}" };
            if (!pr.IsCrossRepository) refspecs.Add($"+refs/heads/{pr.HeadRef}:refs/remotes/{remote}/{pr.HeadRef}");
            return GitActions.FetchRefsAsync(_state.WorkingDirectory, remote, [.. refspecs]);
        });
        if (!ok || _state is null) return;

        var existing = _worktrees.FirstOrDefault(w => w.Branch == pr.HeadRef);
        if (!pr.IsCrossRepository && existing is not null)
        {
            Banner = new Banner($"'{pr.HeadRef}' already has a worktree at {existing.Path}.", false, WorktreeOpenActions(existing));
            return;
        }
        if (!pr.IsCrossRepository && _state.Refs.Any(r => r.Kind == RefKind.LocalBranch && r.Name == pr.HeadRef))
            await CreateWorktreeFromAsync(WorktreeSource.LocalBranch, pr.HeadRef, null, pr.HeadRef);
        else if (!pr.IsCrossRepository)
            await CreateWorktreeFromAsync(WorktreeSource.RemoteBranch, pr.HeadRef, $"{remote}/{pr.HeadRef}", $"{remote}/{pr.HeadRef}");
        else
            await CreateWorktreeFromAsync(WorktreeSource.NewBranch, $"pr/{pr.Number}-{Slug(pr.HeadRef)}", PullRequestRef(pr.Number), $"pull request #{pr.Number}");
    }

    private static string Slug(string branch)
    {
        var name = branch.Contains('/') ? branch[(branch.LastIndexOf('/') + 1)..] : branch;
        var slug = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-', '.');
        return slug.Length == 0 ? "branch" : slug;
    }

    [RelayCommand]
    private static void OpenUrl(string url) => UrlLauncher.Open(url);

    // ------------------------------------------------------------------ tickets

    /// <summary>Opens a ticket ("E4-2361") in Jira, asking for the Jira address the first time.</summary>
    [RelayCommand]
    private async Task OpenTicketAsync(string key)
    {
        if (_state is null) return;
        var site = _settings.ForRepository(_state.MainWorkingDirectory).JiraUrl ?? await AskJiraUrlAsync();
        if (site is not null) UrlLauncher.Open($"{site.TrimEnd('/')}/browse/{Uri.EscapeDataString(key)}");
    }

    [RelayCommand]
    private async Task SetJiraUrlAsync() => await AskJiraUrlAsync();

    /// <summary>Asks for the Jira site this repository's tickets live on and saves it; null when cancelled.</summary>
    private async Task<string?> AskJiraUrlAsync()
    {
        if (_state is null || Dialogs is null) return null;
        var repo = _settings.ForRepository(_state.MainWorkingDirectory);
        var field = new FormField(FormFieldKind.Text, "Jira address") { Text = repo.JiraUrl ?? "", Placeholder = "https://yourcompany.atlassian.net" };
        var spec = new FormSpec("Jira for this repository",
            "Ticket keys in pull request titles (like E4-2361) open in this Jira site.", "Save", [field],
            () => Uri.TryCreate(Normalise(field.Text), UriKind.Absolute, out var u) && u.Scheme is "https" or "http"
                ? null
                : "Enter the address of your Jira site, e.g. https://yourcompany.atlassian.net");
        if (!await Dialogs.ShowFormAsync(spec)) return null;
        repo.JiraUrl = Normalise(field.Text);
        _settings.Save();
        return repo.JiraUrl;

        // "yourcompany.atlassian.net" or a pasted ticket link both become the site's root address.
        static string Normalise(string text)
        {
            var t = text.Trim();
            if (t.Length > 0 && !t.Contains("://", StringComparison.Ordinal)) t = "https://" + t;
            var browse = t.IndexOf("/browse/", StringComparison.OrdinalIgnoreCase);
            return (browse >= 0 ? t[..browse] : t).TrimEnd('/');
        }
    }

    private IReadOnlyList<MenuAction> ActionsForPullRequest(PullRequestSummary pr) =>
    [
        new MenuAction("Open", OpenPullRequestCommand, pr, Icon: MenuIcons.Open),
        new MenuAction("Check out in new worktree…", CheckoutPullRequestCommand, pr, Icon: MenuIcons.Worktree),
        new MenuAction("Open on " + (_prHost?.Kind == HostKind.GitHub ? "GitHub" : "the web"), OpenUrlCommand, pr.Url, Icon: MenuIcons.Browser),
        MenuAction.Separator,
        new MenuAction("Copy link", CopyCommand, pr.Url, Icon: MenuIcons.Copy),
        new MenuAction("Copy branch name", CopyCommand, pr.HeadRef, Icon: MenuIcons.Copy),
        MenuAction.Separator,
        new MenuAction("Set Jira address…", SetJiraUrlCommand),
    ];
}
