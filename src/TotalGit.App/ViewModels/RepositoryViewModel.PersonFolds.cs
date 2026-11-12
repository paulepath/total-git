using CommunityToolkit.Mvvm.Input;
using TotalGit.Core.Git;
using TotalGit.Core.Graph;

namespace TotalGit.App.ViewModels;

// Folding a person: their own branches (commits nobody else has built on) leave the graph and one row stands for
// them, which opens for a look and folds again. Who is folded is remembered per repository; what's open is not.
public partial class RepositoryViewModel
{
    private readonly HashSet<string> _foldedPeople = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folded people whose row is opened for a look, for the session.</summary>
    private readonly HashSet<string> _openPersonFolds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folded people's rows from the last projection, by the row's SHA.</summary>
    private IReadOnlyDictionary<string, PersonFoldInfo> _personFolds = new Dictionary<string, PersonFoldInfo>();

    /// <summary>Commits a folded person's row hides now, mapped to that person's email.</summary>
    private Dictionary<string, string> _personHidden = new(StringComparer.Ordinal);

    /// <summary>
    /// Each folded person's commits that can fold away. HEAD, the main branches, other worktrees and a highlighted
    /// pull request stay whoever wrote them.
    /// </summary>
    private IReadOnlyList<PersonFold> FindPersonFolds(RepositoryState state)
    {
        _personHidden = new Dictionary<string, string>(StringComparer.Ordinal);
        // The filters already narrow the graph to one person or one branch's line: nothing to fold there.
        if (_foldedPeople.Count == 0 || HasFilters) return [];

        var emails = _foldedPeople;
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        if (state.HeadSha is { } head) pinned.Add(head);
        foreach (var r in state.Refs)
        {
            if (r.Kind is RefKind.LocalBranch or RefKind.RemoteBranch
                && (BranchCategory.IsMainLine(r.ShortName) || r.ShortName == state.DefaultBranch))
                pinned.Add(r.TargetSha);
        }
        foreach (var w in OtherWorktrees())
            if (w.HeadSha is { } worktreeHead) pinned.Add(worktreeHead);
        if (_prHighlight is { } pr) pinned.UnionWith(pr.Own);

        var folds = PersonFolds.Find(_commits, emails, pinned);
        foreach (var fold in folds.Where(f => !_openPersonFolds.Contains(f.Email)))
            foreach (var c in fold.Commits)
                _personHidden[c.Sha] = fold.Email;
        return folds;
    }

    /// <summary>
    /// <paramref name="rows"/> with each folded person's row, just above where their newest folded commit is (or
    /// would be).
    /// </summary>
    private IReadOnlyList<CommitInfo> WithPersonFoldRows(IReadOnlyList<CommitInfo> rows, IReadOnlyList<PersonFold> folds, RepositoryState state)
    {
        var infos = new Dictionary<string, PersonFoldInfo>(StringComparer.Ordinal);
        _personFolds = infos;
        if (folds.Count == 0) return rows;

        var present = rows.Select(c => c.Sha).ToHashSet(StringComparer.Ordinal);
        var index = new Dictionary<string, int>(_commits.Count, StringComparer.Ordinal);
        for (var i = 0; i < _commits.Count; i++) index.TryAdd(_commits[i].Sha, i);

        var above = new Dictionary<string, List<CommitInfo>>(StringComparer.Ordinal);
        var atEnd = new List<CommitInfo>();
        foreach (var fold in folds)
        {
            var shas = fold.Commits.Select(c => c.Sha).ToHashSet(StringComparer.Ordinal);
            var branches = state.Refs
                .Where(r => r.Kind is RefKind.LocalBranch or RefKind.RemoteBranch && shas.Contains(r.TargetSha))
                .Select(r => r.ShortName)
                .Distinct(StringComparer.Ordinal)
                .Count();
            var newest = fold.Commits[0];
            var row = CommitInfo.PersonFoldSha(fold.Email);
            infos[row] = new PersonFoldInfo(fold.Email, fold.Name, branches, fold.Commits.Count, _openPersonFolds.Contains(fold.Email), newest.Sha);
            var stub = new CommitInfo(row, [], fold.Name, fold.Email, newest.AuthorDate, "") { IsPersonFold = true };

            // The first row at or after the newest folded commit, in the order the history loads.
            string? anchor = null;
            for (var i = index.GetValueOrDefault(newest.Sha); i < _commits.Count && anchor is null; i++)
                if (present.Contains(_commits[i].Sha)) anchor = _commits[i].Sha;
            if (anchor is null) atEnd.Add(stub);
            else if (above.TryGetValue(anchor, out var stubs)) stubs.Add(stub);
            else above[anchor] = [stub];
        }

        var result = new List<CommitInfo>(rows.Count + folds.Count);
        foreach (var c in rows)
        {
            if (above.TryGetValue(c.Sha, out var stubs)) result.AddRange(stubs);
            result.Add(c);
        }
        result.AddRange(atEnd);
        return result;
    }

    [RelayCommand]
    private void FoldPerson(PersonRef person)
    {
        if (person.Email is not { Length: > 0 } email || _state is null) return;
        _foldedPeople.Add(email);
        _openPersonFolds.Remove(email);
        SaveFilterSettings();
        RebuildGraph();

        var row = CommitInfo.PersonFoldSha(email);
        if (!_personFolds.ContainsKey(row))
        {
            ShowInfo($"{person.Name} has no branches of only their own commits right now, so nothing was folded. Their branches will fold when they do.");
            return;
        }
        if (SelectedSha is { } sha && _personHidden.ContainsKey(sha)) SelectedSha = row;
    }

    [RelayCommand]
    private void StopFoldingPerson(string email)
    {
        if (_state is null) return;
        _foldedPeople.Remove(email);
        _openPersonFolds.Remove(email);
        SaveFilterSettings();
        RebuildGraph();
        if (SelectedSha is { } sha && CommitInfo.PersonFoldOf(sha) is not null) SelectedSha = null;
    }

    /// <summary>Opens a folded person's row for a look, or folds it again.</summary>
    [RelayCommand]
    private void TogglePersonFold(string email)
    {
        if (_state is null) return;
        if (!_openPersonFolds.Remove(email)) _openPersonFolds.Add(email);
        RebuildGraph();
        if (SelectedSha is { } sha && _personHidden.ContainsKey(sha)) SelectedSha = CommitInfo.PersonFoldSha(email);
    }

    /// <summary>The menu of a folded person's row.</summary>
    private IReadOnlyList<MenuAction> PersonFoldRowActions(PersonFoldInfo fold) =>
    [
        fold.Open
            ? new MenuAction($"Fold {fold.Name}'s branches again", TogglePersonFoldCommand, fold.Email, Icon: MenuIcons.Collapse)
            : new MenuAction($"Show {fold.Name}'s branches", TogglePersonFoldCommand, fold.Email, Icon: MenuIcons.Expand),
        new MenuAction($"Stop folding {fold.Name}'s branches", StopFoldingPersonCommand, fold.Email),
    ];

    /// <summary>On a commit's menu: fold its author's branches, or stop folding them.</summary>
    private MenuAction PersonFoldAction(CommitInfo commit) => _foldedPeople.Contains(commit.AuthorEmail)
        ? new MenuAction($"Stop folding {commit.AuthorName}'s branches", StopFoldingPersonCommand, commit.AuthorEmail, Icon: MenuIcons.Expand)
        : new MenuAction($"Fold {commit.AuthorName}'s branches", FoldPersonCommand, new PersonRef(commit.AuthorName, commit.AuthorEmail), Icon: MenuIcons.Collapse);
}
