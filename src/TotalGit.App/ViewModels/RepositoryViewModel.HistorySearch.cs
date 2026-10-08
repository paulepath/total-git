using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.Core.Git;

namespace TotalGit.App.ViewModels;

public sealed partial class RepositoryViewModel
{
    private IReadOnlyList<CommitInfo> _searchMatches = [];
    private int _searchIndex = -1;

    [ObservableProperty]
    public partial string HistoryQuery { get; set; } = "";

    [ObservableProperty]
    public partial string HistorySearchSummary { get; private set; } = "";

    public bool HasSearchMatches => _searchMatches.Count > 0;

    // Typing only counts the matches; Enter, Next and Previous move the selection.
    partial void OnHistoryQueryChanged(string value) => RefreshHistorySearch(reset: true);

    private void RefreshHistorySearch(bool reset = false)
    {
        var previous = !reset && _searchIndex >= 0 && _searchIndex < _searchMatches.Count ? _searchMatches[_searchIndex].Sha : null;
        // With filters on, only the commits they show (folded runs included) count, so a match never clears them.
        var rows = HasFilters && Graph is { } graph ? graph.Layout.Rows.Select(r => r.Commit.Sha).ToHashSet() : null;
        _searchMatches = HistorySearch.Find(rows is null ? _commits : _commits.Where(c => IsShownInGraph(c.Sha, rows)), HistoryQuery);
        _searchIndex = previous is null ? -1 : _searchMatches.ToList().FindIndex(c => c.Sha == previous);
        OnPropertyChanged(nameof(HasSearchMatches));
        NextHistoryMatchCommand.NotifyCanExecuteChanged();
        PreviousHistoryMatchCommand.NotifyCanExecuteChanged();
        UpdateHistorySearchSummary();
    }

    /// <summary>The fuller explanation behind <see cref="HistorySearchSummary"/> (its tooltip).</summary>
    [ObservableProperty]
    public partial string HistorySearchDetail { get; private set; } = "";

    private void UpdateHistorySearchSummary()
    {
        if (string.IsNullOrWhiteSpace(HistoryQuery)) { HistorySearchSummary = HistorySearchDetail = ""; return; }
        var count = _searchMatches.Count;
        // Short, so the filter bar doesn't reflow as you type; the details go in the tooltip.
        HistorySearchSummary = count == 0 ? "No matches" : _searchIndex < 0 ? $"{count} match{(count == 1 ? "" : "es")}" : $"{_searchIndex + 1} of {count}";
        var notes = new List<string> { "Searches messages, SHAs and authors. Enter for the next match, Shift+Enter for the previous." };
        if (HasFilters) notes.Add("Only the commits the filters show are searched.");
        if (_session?.HasMoreHistory == true) notes.Add("Only loaded history is searched: scroll down the graph to load more.");
        HistorySearchDetail = string.Join('\n', notes);
    }

    [RelayCommand(CanExecute = nameof(HasSearchMatches))]
    private void NextHistoryMatch()
    {
        _searchIndex = HistorySearch.Move(_searchIndex, _searchMatches.Count);
        RevealHistoryMatch();
    }

    [RelayCommand(CanExecute = nameof(HasSearchMatches))]
    private void PreviousHistoryMatch()
    {
        _searchIndex = HistorySearch.Move(_searchIndex, _searchMatches.Count, previous: true);
        RevealHistoryMatch();
    }

    private void RevealHistoryMatch()
    {
        if (_searchIndex < 0 || _searchIndex >= _searchMatches.Count) return;
        var sha = _searchMatches[_searchIndex].Sha;
        CloseDiff();
        SelectAndReveal(sha);
        UpdateHistorySearchSummary();
    }

    /// <summary>The commit has its own graph row, or is inside a folded run (selecting it unfolds the run).</summary>
    private bool IsShownInGraph(string sha, HashSet<string> rows) => Graph is { } graph && (rows.Contains(sha)
        || _shownAs.TryGetValue(sha, out var row) && graph.Folds.TryGetValue(row, out var fold) && fold.Shas.Contains(sha));
}
