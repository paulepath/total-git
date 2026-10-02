using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Avatars;
using TotalGit.Core.Git;
using TotalGit.Core.Graph;

namespace TotalGit.App.ViewModels;

/// <summary>One entry of the graph's author filter: everyone, or one developer (by email).</summary>
public sealed class AuthorOption(string? email, string name, int count, bool isMe, Bitmap? avatar)
{
    public string? Email { get; } = email;
    public string Name { get; } = name;
    public bool IsEveryone => Email is null;
    public string Label => IsEveryone ? "Everyone" : isMe ? $"Me · {Name}" : Name;
    public string CountText => IsEveryone ? "" : count.ToString("N0");
    public Bitmap? Avatar { get; } = avatar;
    public bool HasAvatar => Avatar is not null;
    public string Initials => IsEveryone ? "" : AvatarIdentity.Initials(Name);
    public IBrush InitialsBrush => new SolidColorBrush(HslColor.FromHsl(AvatarIdentity.Hue(Email ?? ""), 0.45, 0.42).ToRgb());
}

// The graph's filters (one developer, the current branch's line) and folded runs of commits.
public partial class RepositoryViewModel
{
    private static readonly AuthorOption Everyone = new(null, "Everyone", 0, false, null);

    /// <summary>Runs (newest SHA first) the user folded, and rebased runs they unfolded; kept for the session.</summary>
    private readonly HashSet<string> _foldedByUser = [];
    private readonly HashSet<string> _unfoldedByUser = [];

    /// <summary>The runs found in the last projection, for the "Collapse" menu entries.</summary>
    private IReadOnlyList<CommitFold> _foldableRuns = [];

    /// <summary>Hidden commits mapped to the row that stands for them, from the last projection.</summary>
    private IReadOnlyDictionary<string, string> _shownAs = new Dictionary<string, string>();

    /// <summary>Rows shown and loaded, for the summary; FilteredOut is what the filters (not folds) hide.</summary>
    private int _filteredOut;

    public IReadOnlyList<AuthorOption> Authors { get; private set => SetProperty(ref field, value); } = [Everyone];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters))]
    public partial AuthorOption? SelectedAuthor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters))]
    public partial bool CurrentBranchOnly { get; set; }

    [ObservableProperty]
    public partial bool FoldRuns { get; set; } = true;

    [ObservableProperty]
    public partial string? FilterSummary { get; private set; }

    public bool HasFilters => SelectedAuthor is { IsEveryone: false } || CurrentBranchOnly;

    private bool _loadingFilterSettings;

    partial void OnSelectedAuthorChanged(AuthorOption? value) => OnFiltersChanged();

    partial void OnCurrentBranchOnlyChanged(bool value)
    {
        SaveFilterSettings();
        OnFiltersChanged();
    }

    partial void OnFoldRunsChanged(bool value)
    {
        SaveFilterSettings();
        OnFiltersChanged();
    }

    private void OnFiltersChanged()
    {
        if (_loadingFilterSettings || _state is null) return;
        RebuildGraph();
        _ = FillFilteredViewAsync();
    }

    /// <summary>Another repository opened in this tab: its own saved filters, no developer picked, nothing folded by hand.</summary>
    private void ResetGraphFilters()
    {
        _foldedByUser.Clear();
        _unfoldedByUser.Clear();
        _loadingFilterSettings = true;
        Authors = [Everyone];
        SelectedAuthor = Everyone;
        _loadingFilterSettings = false;
        LoadFilterSettings();
    }

    /// <summary>The loaded commits by SHA, with their real parents (graph rows may have parents rewritten by filters).</summary>
    private Dictionary<string, CommitInfo> LoadedCommitsBySha()
    {
        var bySha = new Dictionary<string, CommitInfo>(_commits.Count);
        foreach (var c in _commits) bySha.TryAdd(c.Sha, c);
        return bySha;
    }

    /// <summary>The repository's saved filter settings, read when it loads.</summary>
    private void LoadFilterSettings()
    {
        if (_state is null) return;
        var repo = _settings.ForRepository(_state.MainWorkingDirectory);
        _loadingFilterSettings = true;
        CurrentBranchOnly = repo.CurrentBranchOnly;
        FoldRuns = repo.FoldRuns;
        _loadingFilterSettings = false;
    }

    private void SaveFilterSettings()
    {
        if (_loadingFilterSettings || _state is null) return;
        var repo = _settings.ForRepository(_state.MainWorkingDirectory);
        repo.CurrentBranchOnly = CurrentBranchOnly;
        repo.FoldRuns = FoldRuns;
        _settings.Save();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _loadingFilterSettings = true;
        SelectedAuthor = Everyone;
        CurrentBranchOnly = false;
        _loadingFilterSettings = false;
        SaveFilterSettings();
        OnFiltersChanged();
    }

    /// <summary>
    /// The loaded history as the graph shows it: the filters' commits only (lines joined across the rest), then runs
    /// of commits folded into one row (rebased runs automatically, others when the user collapses them).
    /// </summary>
    private ProjectedHistory ProjectHistory(RepositoryState state)
    {
        var filtered = HistoryProjection.Project(_commits, KeepFilter(state));
        _filteredOut = filtered.Hidden;

        // Folding is on: every run folds unless the user opened it. Off: only the runs the user collapsed.
        _foldableRuns = CommitRuns.Find(filtered.Commits);
        var folds = _foldableRuns.Where(r => _foldedByUser.Contains(r.Shas[0])
                                             || (FoldRuns && !_unfoldedByUser.Contains(r.Shas[0])))
            .ToList();
        if (folds.Count == 0)
        {
            _shownAs = filtered.ShownAs;
            return filtered with { ShownAs = new Dictionary<string, string>() };
        }

        var folded = HistoryProjection.Project(filtered.Commits, null, folds);
        // Chain the two for revealing a commit: one the filters hide stands in as its row, which may be folded away.
        // The returned map stays fold-only: branch labels move into a fold's row (it holds their commit), but not
        // onto a commit the filters left in place of theirs.
        var shownAs = new Dictionary<string, string>(folded.ShownAs);
        foreach (var (sha, row) in filtered.ShownAs) shownAs[sha] = folded.ShownAs.GetValueOrDefault(row, row);
        _shownAs = shownAs;
        return folded with { Hidden = filtered.Hidden + folded.Hidden };
    }

    /// <summary>Which commits the filters keep, or null when no filter is on.</summary>
    private Func<CommitInfo, bool>? KeepFilter(RepositoryState state)
    {
        var email = SelectedAuthor is { IsEveryone: false } a ? a.Email : null;
        var path = CurrentBranchOnly ? BranchPath(state) : null;
        if (email is null && path is null) return null;
        return c => (email is null || string.Equals(c.AuthorEmail, email, StringComparison.OrdinalIgnoreCase))
                    && (path is null || path.Contains(c.Sha));
    }

    /// <summary>
    /// The current branch's line down to the main branch: the first-parent lines of HEAD, its upstream and the default
    /// branch (local and on origin). Merges on them stay, but not the commits they brought in.
    /// </summary>
    private HashSet<string> BranchPath(RepositoryState state)
    {
        var tips = new List<string?> { state.HeadSha };
        var current = state.Refs.FirstOrDefault(r => r is { Kind: RefKind.LocalBranch, IsCurrent: true });
        if (current?.Upstream is { } upstream) tips.Add(state.Refs.FirstOrDefault(r => r.Kind == RefKind.RemoteBranch && r.Name == upstream)?.TargetSha);
        if (state.DefaultBranch is { } main)
        {
            tips.Add(state.Refs.FirstOrDefault(r => r.Kind == RefKind.LocalBranch && r.Name == main)?.TargetSha);
            tips.Add(state.Refs.FirstOrDefault(r => r.Kind == RefKind.RemoteBranch && r.Name == "origin/" + main)?.TargetSha);
        }
        return FirstParentLines(_commits, tips.OfType<string>());
    }

    /// <summary>
    /// The main-line branches' first-parent lines (those whose branch rule is marked "main line"),
    /// drawn heavier. Worked out on the loaded history, so a line stays heavy across commits the filters hide.
    /// </summary>
    private static HashSet<string> TrunkCommits(IReadOnlyList<CommitInfo> commits, RepositoryState state)
    {
        var tips = state.Refs
            .Where(r => r.Kind is RefKind.LocalBranch or RefKind.RemoteBranch
                        && BranchCategory.IsMainLine(r.ShortName))
            .Select(r => r.TargetSha);
        return FirstParentLines(commits, tips);
    }

    private static HashSet<string> FirstParentLines(IReadOnlyList<CommitInfo> commits, IEnumerable<string> tips)
    {
        var bySha = new Dictionary<string, CommitInfo>(commits.Count);
        foreach (var c in commits) bySha.TryAdd(c.Sha, c);
        var line = new HashSet<string>();
        foreach (var tip in tips)
        {
            // Stop where an earlier line was already walked: the rest is shared.
            for (var at = tip; bySha.TryGetValue(at, out var commit) && line.Add(at);)
            {
                if (commit.ParentShas.Count == 0) break;
                at = commit.ParentShas[0];
            }
        }
        return line;
    }

    /// <summary>The author list for the filter, from the loaded history; kept when it hasn't changed.</summary>
    private void UpdateAuthors(RepositoryState state)
    {
        var people = _commits.Where(c => !c.IsWorkingTree && c.AuthorEmail.Length > 0)
            .GroupBy(c => c.AuthorEmail, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Email: g.Key, Name: g.First().AuthorName, Count: g.Count(), Sample: g.First().Sha))
            .ToList();
        var me = state.UserEmail;
        var ordered = people
            .OrderByDescending(p => string.Equals(p.Email, me, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.Count)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var same = Authors.Count == ordered.Count + 1 && Authors.Skip(1).Zip(ordered).All(x =>
            string.Equals(x.First.Email, x.Second.Email, StringComparison.OrdinalIgnoreCase) && x.First.CountText == x.Second.Count.ToString("N0"));
        if (same) return;

        var github = AvatarIdentity.ParseGitHubRemote(state.OriginUrl);
        var selected = SelectedAuthor?.Email;
        Authors = [Everyone, .. ordered.Select(p => new AuthorOption(p.Email, p.Name, p.Count,
            string.Equals(p.Email, me, StringComparison.OrdinalIgnoreCase), Avatars.TryGet(p.Email, github, p.Sample)))];
        _loadingFilterSettings = true;
        SelectedAuthor = Authors.FirstOrDefault(a => string.Equals(a.Email, selected, StringComparison.OrdinalIgnoreCase)) ?? Everyone;
        _loadingFilterSettings = false;
    }

    private void UpdateFilterSummary(int shown)
    {
        FilterSummary = HasFilters ? $"Showing {shown:N0} of {_commits.Count:N0} loaded commits" : null;
    }

    /// <summary>While the filters hide most of what's loaded, load more history so the graph has something to show.</summary>
    private async Task FillFilteredViewAsync()
    {
        for (var i = 0; i < 10 && HasFilters && _session?.HasMoreHistory == true && (Graph?.Layout.Rows.Count ?? 0) < 200; i++)
            await LoadMoreAsync();
    }

    // ------------------------------------------------------------------ folding

    /// <summary>The run a commit is on, when it can be folded (and isn't already).</summary>
    private CommitFold? FoldableRunAt(string sha) =>
        Graph?.Folds.ContainsKey(sha) == true ? null : CommitRuns.RunContaining(_foldableRuns, sha);

    [RelayCommand]
    private void CollapseRun(CommitFold run)
    {
        _foldedByUser.Add(run.Shas[0]);
        _unfoldedByUser.Remove(run.Shas[0]);
        RebuildGraph();
        if (SelectedSha is { } sha && run.Shas.Contains(sha)) SelectedSha = run.Shas[0];
    }

    [RelayCommand]
    private void ExpandFold(string tipSha)
    {
        _foldedByUser.Remove(tipSha);
        _unfoldedByUser.Add(tipSha);
        RebuildGraph();
    }

    /// <summary>Menu entries for folding: expand a folded row, or collapse the run a commit is on.</summary>
    private IEnumerable<MenuAction> FoldActions(CommitInfo commit)
    {
        if (Graph?.Folds.TryGetValue(commit.Sha, out var fold) == true)
            yield return new MenuAction($"Expand {fold.Count} commits", ExpandFoldCommand, commit.Sha, Icon: MenuIcons.Expand);
        else if (FoldableRunAt(commit.Sha) is { } run)
            yield return new MenuAction($"Collapse these {run.Count} commits", CollapseRunCommand, run, Icon: MenuIcons.Collapse);
    }
}
