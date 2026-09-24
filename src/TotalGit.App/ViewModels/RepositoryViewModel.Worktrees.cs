using CommunityToolkit.Mvvm.ComponentModel;
using TotalGit.Core.Git;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

// WIP rows for the repository's other worktrees: each worktree with uncommitted changes gets a row just above its
// HEAD commit, and selecting it shows those changes read-only.
public sealed partial class RepositoryViewModel
{
    // Other worktrees' changes, by worktree path (only ones with changes).
    private Dictionary<string, WorkingTreeStatus> _otherWip = new(StringComparer.OrdinalIgnoreCase);
    private int _otherWipRequest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWorktreeChanges))]
    public partial WorktreeChangesViewModel? WorktreeChanges { get; set; }

    public bool ShowWorktreeChanges => WorktreeChanges is not null;

    partial void OnWorktreeChangesChanged(WorktreeChangesViewModel? oldValue, WorktreeChangesViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnChildPropertyChanged;
            oldValue.Dispose();
        }
        if (newValue is not null) newValue.PropertyChanged += OnChildPropertyChanged;
    }

    /// <summary>The other worktrees that can have a WIP row: existing folders, not the one this tab shows.</summary>
    private IEnumerable<WorktreeInfo> OtherWorktrees() => _state is not { } state ? [] : _worktrees.Where(w =>
        !w.IsBare && !w.IsPrunable && w.HeadSha is not null && Directory.Exists(w.Path)
        && !WorktreeService.SamePath(w.Path, state.WorkingDirectory));

    /// <summary>
    /// Reads the other worktrees' status (in the background) and redraws the graph if it changed. Called after
    /// loads and refreshes, and when the window is activated again (e.g. back from editing in VS Code).
    /// </summary>
    public async Task RefreshOtherWorktreesAsync()
    {
        var request = ++_otherWipRequest;
        var worktrees = OtherWorktrees().ToList();
        var result = await Task.Run(() =>
        {
            var found = new Dictionary<string, WorkingTreeStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (var wt in worktrees)
            {
                try
                {
                    using var session = RepositorySession.Open(wt.Path);
                    var status = session.GetStatus();
                    if (status.IsDirty) found[wt.Path] = status;
                }
                catch (Exception ex) when (ex is RepositoryOpenException or LibGit2Sharp.LibGit2SharpException or IOException)
                {
                    // Skip a worktree that can't be read right now.
                }
            }
            return found;
        });
        if (request != _otherWipRequest || _state is null) return;

        var changed = result.Count != _otherWip.Count
            || result.Any(kv => !_otherWip.TryGetValue(kv.Key, out var old) || old.TotalCount != kv.Value.TotalCount);
        _otherWip = result;
        if (!changed || Graph is null) return;
        RebuildGraph();

        // The selected WIP row of another worktree: show its new changes, or drop it once it's clean.
        if (SelectedSha is { } sha && WorktreeForWip(sha) is { } selected)
        {
            if (_otherWip.ContainsKey(selected.Path)) await LoadSelectionAsync(sha);
            else SelectedSha = null;
        }
    }

    /// <summary>The loaded history with each other worktree's WIP row inserted just above its HEAD commit.</summary>
    private IEnumerable<CommitInfo> WithOtherWorktreeRows(IReadOnlyList<CommitInfo> commits, Dictionary<string, WipInfo> wip)
    {
        var byHead = OtherWorktrees()
            .Where(w => _otherWip.ContainsKey(w.Path))
            .GroupBy(w => w.HeadSha!)
            .ToDictionary(g => g.Key, g => g.ToList());
        if (byHead.Count == 0) return commits;

        var result = new List<CommitInfo>(commits.Count + byHead.Count);
        foreach (var commit in commits)
        {
            if (byHead.TryGetValue(commit.Sha, out var worktrees))
            {
                foreach (var wt in worktrees)
                {
                    var sha = CommitInfo.OtherWorkingTreeSha(wt.Path);
                    result.Add(new CommitInfo(sha, [commit.Sha], "", "", DateTimeOffset.Now, $"// WIP {wt.Name}",
                        IsWorkingTree: true, WorktreePath: wt.Path));
                    wip[sha] = new WipInfo(_otherWip[wt.Path].TotalCount, wt.Name);
                }
            }
            result.Add(commit);
        }
        return result;
    }

    private WorktreeInfo? WorktreeForWip(string sha) =>
        sha.StartsWith(CommitInfo.WorkingTreeSha + ":", StringComparison.Ordinal)
            ? _worktrees.FirstOrDefault(w => CommitInfo.OtherWorkingTreeSha(w.Path) == sha)
            : null;

    /// <summary>Selecting another worktree's WIP row: read its changes afresh and show them.</summary>
    private async Task LoadWorktreeChangesAsync(WorktreeInfo worktree, int request)
    {
        try
        {
            var (session, status) = await Task.Run(() =>
            {
                var s = RepositorySession.Open(worktree.Path);
                return (s, s.GetStatus());
            });
            if (request != _detailsRequest)
            {
                session.Dispose();
                return;
            }
            WorktreeChanges = new WorktreeChangesViewModel(worktree, session, status);
        }
        catch (Exception ex) when (ex is RepositoryOpenException or LibGit2Sharp.LibGit2SharpException or IOException)
        {
            if (request == _detailsRequest) ShowError($"Couldn't read the worktree at {worktree.Path}: {ex.Message}");
        }
    }

    /// <summary>Opens a folder in its own tab (switching to it if already open); set by the shell.</summary>
    public Func<string, Task>? OpenInTabHandler { get; set; }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task OpenWorktreeInTabAsync(WorktreeInfo worktree) =>
        OpenInTabHandler?.Invoke(worktree.Path) ?? OpenWorktreeAsync(worktree);

    /// <summary>Right-click on another worktree's WIP row.</summary>
    private IReadOnlyList<MenuAction> ActionsForOtherWip(CommitInfo commit)
    {
        if (_worktrees.FirstOrDefault(w => w.Path == commit.WorktreePath) is not { } wt) return [];
        return
        [
            new MenuAction("Open worktree in tab", OpenWorktreeInTabCommand, wt),
            new MenuAction("Open worktree in VS Code", OpenInVsCodeCommand, wt.Path),
            MenuAction.Separator,
            new MenuAction("Copy path", CopyCommand, wt.Path),
        ];
    }

    /// <summary>The folder the diff's file lives in: another worktree's when its changes are shown.</summary>
    private string? DiffFolder => WorktreeChanges?.Path ?? _state?.WorkingDirectory;
}
