using CommunityToolkit.Mvvm.ComponentModel;
using TotalGit.Core.Git;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

// Branches with no commits yet. A branch is only a pointer, so a new one points at the commit it was made from, and
// selecting it used to show that commit (someone else's) as if it were the branch's. Instead its label moves onto a row
// of its own: its WIP row when it's checked out with uncommitted changes, otherwise a "No commits yet" row just above
// the commit it was made from. Selecting that row shows a card saying so.
public sealed partial class RepositoryViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNewBranch))]
    public partial NewBranchCardViewModel? NewBranch { get; set; }

    public bool ShowNewBranch => NewBranch is not null;

    /// <summary>
    /// <paramref name="rows"/> with a row for each new branch that has no WIP row to carry its label, inserted just
    /// above the (shown) commit it was made from. Fills in where each new branch's label goes.
    /// </summary>
    private List<CommitInfo> WithNewBranchRows(IEnumerable<CommitInfo> rows, RepositoryState state, IReadOnlyDictionary<string, string> shownAs,
        bool hasWip, Dictionary<string, string> labelRows, Dictionary<string, NewBranchInfo> newBranches)
    {
        var list = rows.ToList();
        var present = list.Select(c => c.Sha).ToHashSet(StringComparer.Ordinal);
        if (hasWip) present.Add(CommitInfo.WorkingTreeSha);

        var above = new Dictionary<string, List<CommitInfo>>(StringComparer.Ordinal);
        foreach (var branch in state.Refs.Where(r => r is { Kind: RefKind.LocalBranch, IsNew: true }))
        {
            string row;
            if (WipRowOf(branch, state) is { } wip && present.Contains(wip))
            {
                row = wip;
            }
            else
            {
                var commit = shownAs.GetValueOrDefault(branch.TargetSha, branch.TargetSha);
                if (!present.Contains(commit)) continue;
                row = CommitInfo.NewBranchSha(branch.Name);
                newBranches[row] = new NewBranchInfo(branch, branch.CreatedFrom);
                if (!above.TryGetValue(commit, out var stubs)) above[commit] = stubs = [];
                stubs.Add(new CommitInfo(row, [commit], "", "", DateTimeOffset.Now, branch.Name) { NewBranchName = branch.Name });
            }
            labelRows[branch.Name] = row;
            // Pushed but nothing committed yet: its remote branch shares the label.
            if (branch.Upstream is { } upstream
                && state.Refs.FirstOrDefault(r => r.Kind == RefKind.RemoteBranch && r.Name == upstream) is { } remote
                && remote.TargetSha == branch.TargetSha)
                labelRows[remote.Name] = row;
        }
        if (above.Count == 0) return list;

        var result = new List<CommitInfo>(list.Count + above.Count);
        foreach (var c in list)
        {
            if (above.TryGetValue(c.Sha, out var stubs)) result.AddRange(stubs);
            result.Add(c);
        }
        return result;
    }

    /// <summary>The WIP row of the worktree the branch is checked out in, when that has uncommitted changes.</summary>
    private string? WipRowOf(RefInfo branch, RepositoryState state)
    {
        if (branch.IsCurrent) return _status.IsDirty ? CommitInfo.WorkingTreeSha : null;
        return _worktrees.FirstOrDefault(w => w.Branch == branch.Name && !WorktreeService.SamePath(w.Path, state.WorkingDirectory))
            is { } wt && _otherWip.ContainsKey(wt.Path)
            ? CommitInfo.OtherWorkingTreeSha(wt.Path)
            : null;
    }

    /// <summary>
    /// The row to select for a branch or worktree picked in the sidebar: its uncommitted changes when it has any,
    /// else a new branch's own row; null to select the branch's commit as before.
    /// </summary>
    private string? RowForSidebarNode(SidebarNode node)
    {
        if (_state is not { } state || Graph is not { } graph) return null;
        var worktree = node.Worktree ?? node.Target?.Worktree;
        string? row = null;
        if (node.Kind is SidebarNodeKind.LocalBranch or SidebarNodeKind.Worktree)
        {
            var here = worktree is null
                ? node.Kind == SidebarNodeKind.LocalBranch && node.Target?.Name == state.CurrentBranch
                : WorktreeService.SamePath(worktree.Path, state.WorkingDirectory);
            if (here && _status.IsDirty) row = CommitInfo.WorkingTreeSha;
            else if (!here && worktree is not null && _otherWip.ContainsKey(worktree.Path)) row = CommitInfo.OtherWorkingTreeSha(worktree.Path);
        }
        if (row is null && node.Kind == SidebarNodeKind.LocalBranch && node.Target is { } target)
            row = graph.LabelRows.GetValueOrDefault(target.Name);
        return row is not null && graph.Layout.Rows.Any(r => r.Commit.Sha == row) ? row : null;
    }

    /// <summary>Selecting a new branch's row: the "No commits yet" card.</summary>
    private NewBranchCardViewModel CreateNewBranchCard(NewBranchInfo info)
    {
        var branch = info.Branch;
        var worktree = _worktrees.FirstOrDefault(w => w.Branch == branch.Name);
        var here = branch.IsCurrent;
        var start = _commits.FirstOrDefault(c => c.Sha == branch.TargetSha);
        return new NewBranchCardViewModel(info, start,
            checkedOutHere: here,
            otherWorktree: here ? null : worktree,
            showStart: () => _ = SelectShaAsync(branch.TargetSha),
            checkout: here || worktree is not null ? null : () => CheckoutCommand.Execute(BranchTarget.From(branch, null)),
            openWorktree: here || worktree is null ? null : () => OpenWorktreeInTabCommand.Execute(worktree));
    }

    /// <summary>
    /// After the graph is rebuilt: a selected new branch's row that's gone (the branch got a commit, or uncommitted
    /// changes took its label) gives way to where the branch is now.
    /// </summary>
    private void FollowNewBranchSelection()
    {
        if (SelectedSha is not { } sha || CommitInfo.NewBranchOf(sha) is not { } name || Graph is not { } graph
            || graph.NewBranches.ContainsKey(sha))
            return;
        SelectedSha = graph.LabelRows.GetValueOrDefault(name)
                      ?? _state?.Refs.FirstOrDefault(r => r.Kind == RefKind.LocalBranch && r.Name == name)?.TargetSha;
    }
}
