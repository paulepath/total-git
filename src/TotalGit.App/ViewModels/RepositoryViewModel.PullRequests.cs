using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Git;
using TotalGit.Core.Hosting;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

// Pull requests from the repository's hosting service: the sidebar list, opening one in its review window, and
// checking it out into a worktree.
public partial class RepositoryViewModel
{
    private IPullRequestProvider? _prProvider;
    private RemoteHostInfo? _prHost;
    private IReadOnlyList<PullRequestSummary> _pullRequests = [];
    private int _prListRequest;
    private DateTimeOffset _prListLoadedAt;

    /// <summary>Creates pull request providers for supported hosts; set by the shell. Null hides pull requests.</summary>
    public IPullRequestProviderFactory? PullRequestProviders { get; init; }

    /// <summary>Review windows open for this repository's pull requests, by number.</summary>
    private readonly Dictionary<int, PullRequestReviewViewModel> _reviews = [];

    /// <summary>Marks of files reviewed, kept on this machine (shared by every repository).</summary>
    private static readonly Lazy<ReviewMarks> Marks = new(() => new ReviewMarks(Path.Combine(AppSettings.DataDirectory, "review-marks.json")));

    /// <summary>Raised to show a pull request's review window (new, or already open: then it's brought to the front).</summary>
    public event Action<PullRequestReviewViewModel>? ReviewWindowRequested;

    /// <summary>After a repository loads: picks the provider for its remote and lists its pull requests.</summary>
    private void UpdatePullRequestHost()
    {
        var host = PullRequestProviders is null ? null : RemoteHostParser.Parse(_state?.OriginUrl);
        if (host == _prHost) return;
        _prHost = host;
        _prProvider = host is null ? null : PullRequestProviders!.TryCreate(host);
        _pullRequests = [];
        CloseReviewWindows();
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
            LearnJiraProjects(list);
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

    /// <summary>Opens a pull request in its own review window (or brings its window to the front).</summary>
    [RelayCommand]
    private void OpenPullRequest(PullRequestSummary summary)
    {
        if (_prProvider is not { } provider || _state is null || _session is not { } session) return;
        if (_reviews.TryGetValue(summary.Number, out var open))
        {
            ReviewWindowRequested?.Invoke(open);
            return;
        }
        var ticket = PullRequestTriage.Ticket(summary.Title).Key ?? PullRequestTriage.TicketFromBranch(summary.HeadRef,
            _pullRequests.Select(p => PullRequestTriage.Ticket(p.Title).Key).OfType<string>().Select(PullRequestTriage.Project)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
        var ctx = new ReviewContext(provider, session, _state.WorkingDirectory, Marks.Value, _settings,
            CheckoutPullRequestAsync, OpenTicketAsync);
        var vm = new PullRequestReviewViewModel(summary, ctx) { Ticket = ticket };
        _reviews[summary.Number] = vm;
        vm.CloseRequested += () => _reviews.Remove(summary.Number);
        ReviewWindowRequested?.Invoke(vm);
        _ = vm.LoadAsync();
    }

    /// <summary>A review window was closed by the user.</summary>
    public void OnReviewWindowClosed(PullRequestReviewViewModel vm)
    {
        if (_reviews.TryGetValue(vm.Number, out var open) && open == vm) _reviews.Remove(vm.Number);
    }

    /// <summary>The repository went away (tab closed, another repository opened): close its review windows.</summary>
    private void CloseReviewWindows()
    {
        foreach (var vm in _reviews.Values.ToList()) vm.Close();
        _reviews.Clear();
    }

    /// <summary>Where a pull request's head is fetched to (outside refs/heads and refs/remotes, so it isn't listed as a branch).</summary>
    private static string PullRequestRef(int number) => PullRequestReviewViewModel.PullRequestRef(number);

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
