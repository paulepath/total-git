using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.Core.Git;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

/// <summary>The commits picked with Shift+click, shown in the right-hand panel instead of one commit's details.</summary>
public sealed class CommitRangeViewModel(CommitRangeResult range, IReadOnlyList<string> tipBranches, string? containingBranch)
{
    public CommitRangeResult Range { get; } = range;
    public IReadOnlyList<RangeCommitItem> Commits { get; } = range.Commits.Select(c => new RangeCommitItem(c)).ToList();

    public string Title => Commits.Count == 1 ? "1 commit selected" : $"{Commits.Count} commits selected";

    /// <summary>Squashing needs at least two commits (whether they're on the checked-out branch is checked when asked).</summary>
    public bool CanSquash => Commits.Count > 1;

    public string Subtitle => tipBranches.Count > 0
        ? $"Ending at the tip of {string.Join(", ", tipBranches)}."
        : $"Below the tip of {containingBranch ?? "a branch"}: rebasing puts them in a new branch.";
}

// Rebasing a run of commits picked in the graph onto another branch: `git rebase --onto <target> <oldest^> <branch>`.
public partial class RepositoryViewModel
{
    /// <summary>The SHAs of the picked run (shown highlighted in the graph), or null.</summary>
    [ObservableProperty]
    public partial IReadOnlyCollection<string>? SelectedRange { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRange), nameof(ShowDetails))]
    public partial CommitRangeViewModel? Range { get; private set; }

    public bool ShowRange => Range is not null;

    partial void OnSelectedRangeChanged(IReadOnlyCollection<string>? value)
    {
        if (value is null) Range = null;
    }

    /// <summary>Shift+click in the graph: picks the commits from <paramref name="anchor"/> to <paramref name="other"/>.</summary>
    public void SelectRange(string anchor, string other)
    {
        if (Graph is null) return;
        var range = CommitRange.Resolve(LoadedCommitsBySha(), anchor, other);
        if (!range.IsValid)
        {
            ShowError(range.Error!);
            return;
        }
        Diff = null;
        Range = new CommitRangeViewModel(range, TipBranches(range.Newest.Sha).Select(b => b.Name).ToList(), ContainingBranch(range.Newest.Sha));
        SelectedRange = range.Commits.Select(c => c.Sha).ToHashSet();
    }

    /// <summary>A local branch with the commit on its first-parent line (the current branch first), from the loaded history.</summary>
    private string? ContainingBranch(string sha)
    {
        if (Graph is null || _state is null) return null;
        var bySha = LoadedCommitsBySha();
        foreach (var branch in _state.Refs.Where(r => r.Kind == RefKind.LocalBranch).OrderByDescending(r => r.IsCurrent))
        {
            for (var at = branch.TargetSha; bySha.TryGetValue(at, out var commit);)
            {
                if (at == sha) return branch.Name;
                if (commit.ParentShas.Count == 0) break;
                at = commit.ParentShas[0];
            }
        }
        return null;
    }

    [RelayCommand]
    private void ClearRange() => SelectedRange = null;

    [RelayCommand]
    private Task CopyRangeShas() => CopyAsync(string.Join(Environment.NewLine, Range?.Range.Commits.Select(c => c.Sha).Reverse() ?? []));

    private List<RefInfo> TipBranches(string sha) => _state?.Refs
        .Where(r => r.Kind == RefKind.LocalBranch && r.TargetSha == sha)
        .OrderByDescending(r => r.IsCurrent)
        .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .ToList() ?? [];

    /// <summary>Menu entries for a commit inside the picked run.</summary>
    private IReadOnlyList<MenuAction>? ActionsForRange(CommitInfo commit) =>
        SelectedRange?.Contains(commit.Sha) == true && Range is { } range
            ?
            [
                new MenuAction(range.Commits.Count == 1 ? "Rebase selected commit onto…" : $"Rebase {range.Commits.Count} selected commits onto…",
                    RebaseRangeCommand, IsEnabled: !IsOperationInProgress, Icon: MenuIcons.Rebase),
                new MenuAction($"Squash {range.Commits.Count} commits into one…", SquashRangeCommand,
                    IsEnabled: range.Commits.Count > 1 && !IsOperationInProgress && _state?.CurrentBranch is not null, Icon: MenuIcons.Squash),
                new MenuAction("Copy SHAs", CopyRangeShasCommand, Icon: MenuIcons.Copy),
                MenuAction.Separator,
                new MenuAction("Clear selection", ClearRangeCommand),
            ]
            : null;

    /// <summary>Replays only the picked commits onto a branch the user chooses.</summary>
    /// <param name="target">The branch the commits were dragged onto, pre-filled in the dialog; null to pick one there.</param>
    [RelayCommand]
    private async Task RebaseRangeAsync(string? target)
    {
        if (_state is null || Dialogs is null || Range is not { } picked || !EnsureCleanFor("rebase")) return;
        var state = _state;
        var wt = state.WorkingDirectory;
        var commits = picked.Range.Commits;
        var oldest = picked.Range.Oldest;
        var upstream = oldest.ParentShas.FirstOrDefault();

        var locals = state.Refs.Where(r => r.Kind == RefKind.LocalBranch).ToList();
        var remotes = state.Refs.Where(r => r.Kind == RefKind.RemoteBranch && !r.Name.EndsWith("/HEAD", StringComparison.Ordinal)).ToList();
        var byName = locals.Concat(remotes).GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First());
        var subjects = LoadedCommitsBySha().ToDictionary(p => p.Key, p => p.Value.MessageShort);

        var tips = TipBranches(picked.Range.Newest.Sha).Select(r =>
        {
            var other = _worktrees.FirstOrDefault(w => w.Branch == r.Name && !WorktreeService.SamePath(w.Path, wt));
            return new BranchChoice(r.Name, r.Upstream is not null, other?.Path);
        }).ToList();

        // Onto: the moved branch's base if it tracks one, else main/master, else the first branch.
        var targets = locals.Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Concat(remotes.Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)).ToList();
        var defaultTarget = target is not null && byName.ContainsKey(target) ? target
            : state.DefaultBranch is { } main && byName.ContainsKey(main) ? main
            : new[] { "main", "master", "develop" }.FirstOrDefault(byName.ContainsKey)
            ?? targets.FirstOrDefault(t => tips.All(b => b.Name != t)) ?? "";

        var vm = new RebaseCommitsViewModel(commits, tips, targets, defaultTarget, locals.Select(r => r.Name).ToHashSet(), state.CurrentBranch,
            ContainingBranch(picked.Range.Newest.Sha),
            describeTarget: name => byName.TryGetValue(name, out var r)
                ? $"{r.TargetSha[..7]} {(subjects.TryGetValue(r.TargetSha, out var s) ? s : "")}".Trim()
                : null,
            droppedCommits: (target, branch) => upstream is null
                ? Task.FromResult<IReadOnlyList<string>>([])
                : GitActions.CommitSummariesAsync(wt, $"{target}..{upstream}"));
        await vm.InitializeAsync();
        if (!await Dialogs.ShowRebaseCommitsAsync(vm)) return;

        target = vm.Target.Trim();
        var branch = vm.ResultBranch;
        var count = commits.Count == 1 ? "1 commit" : $"{commits.Count} commits";
        var outcome = OperationOutcome.Completed;
        var ok = await RunGitAsync($"Rebasing {count} onto {target}…", async () =>
        {
            if (!vm.MoveExisting) await GitActions.CreateBranchAsync(wt, branch, picked.Range.Newest.Sha, checkout: false);
            outcome = await GitActions.RebaseOntoAsync(wt, target, upstream, branch);
        });
        SelectedRange = null;
        if (!ok) return;
        OnStopped(outcome);
        if (outcome != OperationOutcome.Completed) return;

        // git drops commits whose changes the target already has (e.g. squash-merged ones): say so.
        var landed = (await GitActions.CommitSummariesAsync(wt, $"{target}..{branch}")).Count;
        var dropped = commits.Count - landed;
        var message = $"Rebased {count} onto {target}: {branch} now ends at them.";
        if (dropped > 0)
            message = $"Rebased onto {target}: {landed} of {count} applied to {branch}. " +
                      $"{(dropped == 1 ? "1 was" : $"{dropped} were")} left out because {target} already has {(dropped == 1 ? "its" : "their")} changes.";
        var hadUpstream = vm.MoveExisting && vm.BranchToMove?.HasUpstream == true;
        Banner = new Banner(message, false, hadUpstream ? [new MenuAction("Force push…", ForcePushCommand)] : []);
        if (_state?.HeadSha is { } head) SelectAndReveal(head);
    }

    /// <summary>Squashes the picked commits (on the checked-out branch) into one, with a message the user edits.</summary>
    [RelayCommand]
    private async Task SquashRangeAsync()
    {
        if (_state is null || _session is not { } session || Dialogs is null || Range is not { } picked || !EnsureCleanFor("squash")) return;
        var wt = _state.WorkingDirectory;
        var oldestFirst = picked.Range.Commits.Select(c => c.Sha).Reverse().ToList();
        var count = oldestFirst.Count;

        if (await GitActions.SquashProblemAsync(wt, oldestFirst) is { } problem)
        {
            ShowError(problem);
            return;
        }

        // Start from every message, oldest first, as git does when squashing.
        var messages = await Task.Run(() => oldestFirst.Select(sha => session.GetCommitDetails(sha).FullMessage.Trim()).ToList());
        var pushed = (await GitActions.PushedCommitsAsync(wt, null)).Count(oldestFirst.Contains);
        var message = new FormField(FormFieldKind.MultilineText, "Message") { Text = string.Join("\n\n", messages), Height = 220 };
        var note = $"Combines the {count} selected commits on {_state.CurrentBranch} into one commit. Their changes stay as they are.";
        if (pushed > 0)
            note += pushed == count
                ? " They're already pushed, so you'll need to force push afterwards."
                : $" {pushed} of them are already pushed, so you'll need to force push afterwards.";
        var spec = new FormSpec($"Squash {count} commits", note, "Squash", [message],
            () => string.IsNullOrWhiteSpace(message.Text) ? "Enter a commit message." : null);
        if (!await Dialogs.ShowFormAsync(spec)) return;

        var outcome = OperationOutcome.Completed;
        var ok = await RunGitAsync($"Squashing {count} commits…", async () => outcome = await GitActions.SquashAsync(wt, oldestFirst, message.Text.Trim()));
        SelectedRange = null;
        if (!ok) return;
        OnStopped(outcome);
        if (outcome != OperationOutcome.Completed) return;

        Banner = new Banner($"Squashed {count} commits into one.", false, pushed > 0 ? [new MenuAction("Force push…", ForcePushCommand)] : []);
        if (_state?.HeadSha is { } head) SelectAndReveal(head);
    }
}
