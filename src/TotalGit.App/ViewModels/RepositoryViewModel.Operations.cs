using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.Core.Git;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

/// <summary>Branch, tag, stash, merge and rebase commands.</summary>
public partial class RepositoryViewModel
{
    // ------------------------------------------------------------------ branches

    [RelayCommand]
    private async Task DeleteBranchAsync(BranchTarget target)
    {
        if (_state is null || Dialogs is null || target.Kind != RefKind.LocalBranch) return;
        if (!await Dialogs.ConfirmAsync("Delete branch", $"Delete the local branch '{target.Name}'?", null, "Delete"))
            return;

        var wt = _state.WorkingDirectory;
        try
        {
            await GitActions.DeleteBranchAsync(wt, target.Name);
            ShowInfo($"Deleted {target.Name}.");
        }
        catch (GitCommandException ex) when (ex.Message.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
        {
            if (await Dialogs.ConfirmAsync("Branch not merged",
                    $"'{target.Name}' has commits that aren't merged into the current branch or its upstream. Delete it anyway? Those commits will only be reachable from the reflog.",
                    null, "Delete anyway"))
                await RunGitAsync($"Deleting {target.Name}…", () => GitActions.DeleteBranchAsync(wt, target.Name, force: true), $"Deleted {target.Name}.");
        }
        catch (GitCommandException ex)
        {
            ShowError(ex.Message);
        }
        await RefreshRefsAsync();
    }

    /// <summary>Deletes a branch on its remote, for everyone. Local branches (even ones tracking it) are kept.</summary>
    [RelayCommand]
    private async Task DeleteRemoteBranchAsync(BranchTarget target)
    {
        if (_state is null || Dialogs is null || target is not { Kind: RefKind.RemoteBranch, RemoteName: { } remote }) return;
        var branch = target.ShortName;
        var details = new List<string>();
        var tip = _commits.FirstOrDefault(c => c.Sha == target.Sha);
        if (tip is not null) details.Add($"Newest commit: {tip.ShortSha} {tip.MessageShort} ({tip.AuthorName})");
        if (_pullRequests.FirstOrDefault(p => !p.IsCrossRepository && p.HeadRef == branch) is { } pr)
            details.Add($"Pull request #{pr.Number} \"{pr.Title}\" uses this branch: deleting it closes the pull request.");
        var tracking = _state.Refs.Where(r => r.Kind == RefKind.LocalBranch && r.Upstream == target.Name).Select(r => r.Name).ToList();
        if (tracking.Count > 0) details.Add($"Kept: your local branch {string.Join(", ", tracking)} (it will show as deleted on the remote).");

        if (!await Dialogs.ConfirmAsync("Delete branch from remote",
                $"Delete '{branch}' from {remote}? This removes it for everyone who uses {remote}.", details, "Delete from remote"))
            return;
        if (BranchCategory.IsMainLine(branch) && !await Dialogs.ConfirmAsync("Delete a main line",
                $"'{branch}' is a main line (its branch rule marks it as one). Other branches are probably based on it. Really delete it from {remote}?",
                null, "Delete it"))
            return;

        var wt = _state.WorkingDirectory;
        await RunGitAsync($"Deleting {branch} from {remote}…", () => GitActions.DeleteRemoteBranchAsync(wt, remote, branch),
            $"Deleted {branch} from {remote}.");
    }

    /// <summary>Local branches whose remote branch was deleted and which aren't checked out anywhere.</summary>
    private List<RefInfo> GoneBranches() => _state?.Refs
        .Where(r => r is { Kind: RefKind.LocalBranch, UpstreamGone: true, IsCurrent: false }
                    && !_worktrees.Any(w => w.Branch == r.Name))
        .ToList() ?? [];

    /// <summary>
    /// Fetches (pruning deleted remote branches), then lets the user pick which local branches whose remote branch
    /// is gone to delete. The current branch and branches checked out in other worktrees are listed but can't be picked.
    /// </summary>
    [RelayCommand]
    private async Task CleanUpBranchesAsync()
    {
        if (_state is null || Dialogs is null) return;
        var wt = _state.WorkingDirectory;

        var fetchFailed = false;
        IsBusy = true;
        BusyText = "Fetching and pruning…";
        try
        {
            await GitActions.FetchAsync(wt);
        }
        catch (Exception ex) when (ex is GitCommandException or IOException or System.ComponentModel.Win32Exception)
        {
            fetchFailed = true;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
        await RefreshRefsAsync();
        if (_state is null) return;

        var gone = _state.Refs.Where(r => r is { Kind: RefKind.LocalBranch, UpstreamGone: true }).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (gone.Count == 0)
        {
            ShowInfo(fetchFailed
                ? "Couldn't reach the remote. No local branches had a deleted remote branch at the last fetch."
                : "No local branches have a deleted remote branch. Nothing to clean up.");
            return;
        }

        IReadOnlyList<BranchSummary> summaries;
        try
        {
            summaries = await GitActions.GetBranchSummariesAsync(wt, gone.Select(r => r.Name));
        }
        catch (GitCommandException)
        {
            summaries = [];
        }
        var items = gone.Select(r =>
        {
            var summary = summaries.FirstOrDefault(s => s.Name == r.Name);
            var otherWorktree = _worktrees.FirstOrDefault(w => w.Branch == r.Name && !WorktreeService.SamePath(w.Path, wt));
            var reason = r.IsCurrent ? "The branch you're on: switch to another branch first."
                : otherWorktree is not null ? $"Checked out in the worktree {otherWorktree.Name}: remove the worktree first."
                : null;
            return new BranchCleanupItem(r.Name, r.Upstream, summary?.Subject ?? "", summary?.LastCommit, summary?.UnpushedCount ?? 0, reason);
        });

        var dialog = new BranchCleanupViewModel(items, fetchFailed);
        if (!await Dialogs.ShowBranchCleanupAsync(dialog)) return;

        var selected = dialog.Selected.Select(i => i.Name).ToList();
        var failed = new List<string>();
        IsBusy = true;
        BusyText = $"Deleting {selected.Count} branch{(selected.Count == 1 ? "" : "es")}…";
        try
        {
            // Keep going past a failure, so one stuck branch doesn't leave the rest behind.
            foreach (var name in selected)
            {
                try
                {
                    await GitActions.DeleteBranchAsync(wt, name, force: true);
                }
                catch (GitCommandException ex)
                {
                    failed.Add($"{name} ({ex.Message.Trim()})");
                }
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }

        var deleted = selected.Count - failed.Count;
        var message = $"Deleted {deleted} branch{(deleted == 1 ? "" : "es")}.";
        if (failed.Count > 0) ShowError($"{message} Couldn't delete {string.Join(", ", failed)}.");
        else ShowInfo(message);
        await RefreshRefsAsync();
    }

    // ------------------------------------------------------------------ merge / rebase

    /// <summary>Shown while a merge or rebase is stopped (usually on conflicts), until it's continued or aborted.</summary>
    [ObservableProperty]
    public partial Banner? OperationBanner { get; set; }

    private bool IsOperationInProgress => _state is { Operation: not RepoOperation.None };

    /// <summary>Merge/rebase need a clean working tree (untracked files are fine).</summary>
    private bool EnsureCleanFor(string what)
    {
        if (!_status.Staged.Any() && _status.Unstaged.All(f => f.Kind == ChangeKind.Untracked)) return true;
        Banner = new Banner($"Commit or stash your changes before you {what}.", true, [new MenuAction("Stash changes…", StashCommand)]);
        return false;
    }

    private void UpdateOperationBanner()
    {
        RenderOperationBanner();
        _ = CheckRebaseBlockAsync();
    }

    // A rebase step git keeps failing to start because untracked files are in its way (see RebaseBlock).
    private BlockedPick? _blockedPick;
    private bool _checkingRebaseBlock;

    /// <summary>
    /// Looks for a rebase stuck on untracked files (to say so in the banner) and, once no rebase is running, puts
    /// back files moved aside for one.
    /// </summary>
    private async Task CheckRebaseBlockAsync()
    {
        if (_state is not { } state || _checkingRebaseBlock) return;
        var wt = state.WorkingDirectory;
        _checkingRebaseBlock = true;
        try
        {
            if (state.Operation == RepoOperation.Rebase && !_status.Unstaged.Any(f => f.Kind == ChangeKind.Conflicted))
            {
                var blocked = await RebaseBlock.FindAsync(wt);
                if (_state?.WorkingDirectory != wt || SameBlock(blocked, _blockedPick)) return;
                _blockedPick = blocked;
                RenderOperationBanner();
                return;
            }
            if (_blockedPick is not null)
            {
                _blockedPick = null;
                RenderOperationBanner();
            }
            if (state.Operation == RepoOperation.None && await RebaseBlock.RestoreAsync(wt) is { } restored)
                ReportRestored(restored);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GitCommandException)
        {
            // The rebase moved on (or a file is locked) mid-check; the next refresh looks again.
        }
        finally
        {
            _checkingRebaseBlock = false;
        }
    }

    private static bool SameBlock(BlockedPick? a, BlockedPick? b) =>
        a is null ? b is null : b is not null && a.Sha == b.Sha && a.UntrackedInTheWay.SequenceEqual(b.UntrackedInTheWay);

    private void ReportRestored(RestoreResult result)
    {
        if (result.LeftAside.Count == 0)
        {
            ShowInfo($"Put back {Files(result.Restored)} moved aside for the rebase.");
            return;
        }
        Banner = new Banner(
            $"{Files(result.LeftAside)} moved aside for the rebase couldn't be put back, as something is at {(result.LeftAside.Count == 1 ? "its path" : "their paths")} now: " +
            $"{List(result.LeftAside)}. {(result.LeftAside.Count == 1 ? "It's" : "They're")} kept in {result.Folder}.",
            true, [new MenuAction("Open folder", RevealCommand, result.Folder)]);
    }

    private static string Files(IReadOnlyCollection<string> paths) => paths.Count == 1 ? "1 file" : $"{paths.Count} files";

    /// <summary>The first few file names, then how many more.</summary>
    private static string List(IReadOnlyList<string> paths)
    {
        var names = string.Join(", ", paths.Take(3).Select(Path.GetFileName));
        return paths.Count > 3 ? $"{names} and {paths.Count - 3} more" : names;
    }

    private void RenderOperationBanner()
    {
        var state = _state;
        if (state is null || state.Operation == RepoOperation.None)
        {
            OperationBanner = null;
            return;
        }

        var conflicts = _status.Unstaged.Count(f => f.Kind == ChangeKind.Conflicted);
        var conflictText = conflicts switch
        {
            0 => "No conflicts left.",
            1 => "1 conflicted file. Select it in the WIP row to resolve it.",
            _ => $"{conflicts} conflicted files. Select them in the WIP row to resolve them.",
        };
        var resolved = conflicts == 0;
        OperationBanner = state.Operation switch
        {
            RepoOperation.Merge => new Banner($"Merge in progress. {conflictText}", false,
            [
                new MenuAction("Continue merge", ContinueOperationCommand, IsEnabled: resolved),
                new MenuAction("Abort merge", AbortOperationCommand),
            ]),
            RepoOperation.Rebase when resolved && _blockedPick is { UntrackedInTheWay.Count: > 0 } blocked => new Banner(
                $"Rebase stuck{(state.OperationProgress is { } at ? $" at commit {at}" : "")}: “{blocked.Subject}” adds " +
                $"{Files(blocked.UntrackedInTheWay)} that already exist here untracked ({List(blocked.UntrackedInTheWay)}), and git won't " +
                "overwrite them. Continuing only tries again.", true,
            [
                new MenuAction("Move them aside and continue…", MoveAsideAndContinueCommand),
                new MenuAction("Abort rebase", AbortOperationCommand),
            ]),
            RepoOperation.Rebase when resolved && _blockedPick is not null => new Banner(
                $"Rebase stuck{(state.OperationProgress is { } at ? $" at commit {at}" : "")}: git can't start “{_blockedPick.Subject}” " +
                "and keeps putting it back, so Continue only tries again. Run 'git rebase --continue' in a terminal to see git's reason.", true,
            [
                new MenuAction("Continue rebase", ContinueOperationCommand),
                new MenuAction("Skip this commit", SkipRebaseCommitCommand),
                new MenuAction("Abort rebase", AbortOperationCommand),
            ]),
            RepoOperation.Rebase => new Banner(
                $"Rebase paused{(state.OperationProgress is { } p ? $" at commit {p}" : "")}. {conflictText}", false,
            [
                new MenuAction("Continue rebase", ContinueOperationCommand, IsEnabled: resolved),
                new MenuAction("Skip this commit", SkipRebaseCommitCommand),
                new MenuAction("Abort rebase", AbortOperationCommand),
            ]),
            _ => new Banner($"A {(state.Operation == RepoOperation.CherryPick ? "cherry-pick" : "revert")} is in progress. {conflictText} " +
                            "Finish or abort it from a terminal.", false, []),
        };
    }

    /// <summary>After a merge or rebase stops, show the WIP row so the conflicted files are one click away.</summary>
    private void OnStopped(OperationOutcome outcome)
    {
        if (outcome == OperationOutcome.Stopped) SelectedSha = CommitInfo.WorkingTreeSha;
    }

    // ------------------------------------------------------------------ merge tool

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMergeTool))]
    public partial MergeToolViewModel? MergeTool { get; set; }

    public bool HasMergeTool => MergeTool is not null;

    private int _mergeToolRequest;

    private async void OpenMergeTool(string path)
    {
        if (_state is null) return;
        var wt = _state.WorkingDirectory;
        var request = ++_mergeToolRequest;
        try
        {
            var tool = await MergeToolViewModel.LoadAsync(wt, path, Settings,
                markResolved: async p =>
                {
                    var ok = await RunGitAsync("Marking resolved…", () => GitActions.StageAsync(wt, [p]), $"Resolved {p}.", Refresh.Status);
                    if (ok) SelectNextConflict();
                    return ok;
                },
                takeWholeFile: async (p, ours) =>
                {
                    if (await RunGitAsync("Resolving…", () => GitActions.TakeSideAsync(wt, p, ours), $"Resolved {p}.", Refresh.Status))
                        SelectNextConflict();
                },
                confirm: (title, message, confirmText) => Dialogs?.ConfirmAsync(title, message, null, confirmText) ?? Task.FromResult(false),
                close: () =>
                {
                    if (Staging is not null) Staging.SelectedFile = null;
                    MergeTool = null;
                });
            // Another file was picked (or the tool closed) while this one loaded.
            if (request != _mergeToolRequest
                || Staging is not { SelectedFile: { Change.Kind: ChangeKind.Conflicted } selected } || selected.Path != path)
                return;
            MergeTool = tool;
        }
        catch (IOException ex)
        {
            ShowError(ex.Message);
        }
    }

    /// <summary>After resolving a file, open the next conflicted one (or close the tool when none are left).</summary>
    private void SelectNextConflict()
    {
        if (Staging is null) return;
        var next = Staging.Unstaged.FirstOrDefault(f => f.Change.Kind == ChangeKind.Conflicted);
        MergeTool = null;
        Staging.SelectedFile = next;
    }

    [RelayCommand]
    private async Task MergeAsync(BranchTarget target)
    {
        if (_state is null || Dialogs is null || !EnsureCleanFor("merge")) return;
        var current = _state.CurrentBranch ?? "HEAD";
        var name = target.Kind == RefKind.DetachedHead ? $"commit {target.Name}" : target.Name;
        var revision = target.Kind == RefKind.DetachedHead ? target.Sha : target.Name;
        var noFf = FormField.CheckBox("Always create a merge commit (even when a fast-forward is possible)");
        if (!await Dialogs.ShowFormAsync(new FormSpec("Merge", $"Merge {name} into {current}.", "Merge", [noFf])))
            return;

        var wt = _state.WorkingDirectory;
        var outcome = OperationOutcome.Completed;
        await RunGitAsync($"Merging {name}…", async () => outcome = await GitActions.MergeAsync(wt, revision, noFf.IsChecked));
        if (outcome == OperationOutcome.Completed) ShowInfo($"Merged {name} into {current}.");
        OnStopped(outcome);
    }

    [RelayCommand]
    private async Task RebaseOntoAsync(BranchTarget target)
    {
        if (_state is null || Dialogs is null || !EnsureCleanFor("rebase")) return;
        var current = _state.CurrentBranch ?? "HEAD";
        if (!await Dialogs.ConfirmAsync("Rebase",
                $"Rebase {current} onto {target.Name}? The commits on {current} that aren't in {target.Name} are replayed on top of it. " +
                "This rewrites their history; if they were already pushed you'll need to force-push.",
                null, "Rebase"))
            return;

        var wt = _state.WorkingDirectory;
        var onto = target.Kind == RefKind.DetachedHead ? target.Sha : target.Name;
        var outcome = OperationOutcome.Completed;
        await RunGitAsync($"Rebasing onto {target.Name}…", async () => outcome = await GitActions.RebaseAsync(wt, onto));
        if (outcome == OperationOutcome.Completed) ShowInfo($"Rebased {current} onto {target.Name}.");
        OnStopped(outcome);
    }

    /// <summary>Rewrites the current branch from <paramref name="commit"/> (included) up to HEAD.</summary>
    [RelayCommand]
    private async Task InteractiveRebaseAsync(CommitInfo commit)
    {
        if (_state is null || Dialogs is null || !EnsureCleanFor("rebase")) return;
        var wt = _state.WorkingDirectory;
        var branch = _state.CurrentBranch ?? "HEAD";
        if (!await GitActions.IsAncestorAsync(wt, commit.Sha, "HEAD"))
        {
            ShowError($"{commit.ShortSha} isn't part of {branch}, so it can't be rebased from here. Check out a branch that contains it first.");
            return;
        }

        var baseSha = commit.ParentShas.FirstOrDefault();
        var commits = await GitActions.CommitsSinceAsync(wt, baseSha);
        if (commits.Any(c => c.IsMerge))
        {
            ShowError("There are merge commits between here and the branch tip. Interactive rebase of merges isn't supported; pick a later commit.");
            return;
        }
        var pushed = await GitActions.PushedCommitsAsync(wt, baseSha);
        var vm = new InteractiveRebaseViewModel(branch,
            baseSha is null ? "from its first commit." : $"from {commit.ShortSha} \u201c{commit.MessageShort}\u201d up to its tip.",
            commits.Select(c => new RebaseRow(c.Sha, c.Subject, pushed.Contains(c.Sha))));
        if (!await Dialogs.ShowInteractiveRebaseAsync(vm)) return;
        if (vm.IsUnchanged(commits.Select(c => c.Sha).ToList()))
        {
            ShowInfo("Nothing to change.");
            return;
        }

        var outcome = OperationOutcome.Completed;
        var ok = await RunGitAsync($"Rebasing {branch}…", async () => outcome = await GitActions.InteractiveRebaseAsync(wt, baseSha, vm.Steps()));
        if (ok && outcome == OperationOutcome.Completed) ShowInfo($"Rewrote {branch}.");
        OnStopped(outcome);
    }

    [RelayCommand]
    private async Task ContinueOperationAsync()
    {
        if (_state is null) return;
        var wt = _state.WorkingDirectory;
        var op = _state.Operation;
        var outcome = OperationOutcome.Completed;
        var ok = await RunGitAsync("Continuing…", async () =>
        {
            if (op == RepoOperation.Merge) await GitActions.MergeContinueAsync(wt);
            else outcome = await GitActions.RebaseContinueAsync(wt);
        });
        if (ok && outcome == OperationOutcome.Completed) ShowInfo(op == RepoOperation.Merge ? "Merge completed." : "Rebase completed.");
        OnStopped(outcome);
    }

    /// <summary>
    /// A rebase stuck on untracked files: moves them into the worktree's git folder and continues. They're put back
    /// once no rebase is running (finished or aborted).
    /// </summary>
    [RelayCommand]
    private async Task MoveAsideAndContinueAsync()
    {
        if (_state is null || Dialogs is null || _blockedPick is not { UntrackedInTheWay.Count: > 0 } blocked) return;
        if (!await Dialogs.ConfirmAsync("Move files aside",
                $"“{blocked.Subject}” adds these files, but they already exist here untracked. They're moved into the worktree's " +
                "git folder so the rebase can carry on, and put back where they were when the rebase is over (finished or aborted). " +
                "If something is at a path by then, that file stays aside and you're told where.",
                blocked.UntrackedInTheWay, "Move aside and continue"))
            return;
        var wt = _state.WorkingDirectory;
        var files = blocked.UntrackedInTheWay;
        var outcome = OperationOutcome.Completed;
        var ok = await RunGitAsync("Continuing…", async () =>
        {
            await RebaseBlock.MoveAsideAsync(wt, files);
            outcome = await GitActions.RebaseContinueAsync(wt);
        });
        if (ok && outcome == OperationOutcome.Completed) ShowInfo("Rebase completed.");
        OnStopped(outcome);
    }

    [RelayCommand]
    private async Task SkipRebaseCommitAsync()
    {
        if (_state is null || Dialogs is null) return;
        if (!await Dialogs.ConfirmAsync("Skip commit", "Skip the commit being replayed? Its changes are left out of the rebased branch.", null, "Skip commit"))
            return;
        var wt = _state.WorkingDirectory;
        var outcome = OperationOutcome.Completed;
        var ok = await RunGitAsync("Skipping…", async () => outcome = await GitActions.RebaseSkipAsync(wt));
        if (ok && outcome == OperationOutcome.Completed) ShowInfo("Rebase completed.");
        OnStopped(outcome);
    }

    [RelayCommand]
    private async Task AbortOperationAsync()
    {
        if (_state is null || Dialogs is null) return;
        var op = _state.Operation;
        var what = op == RepoOperation.Merge ? "merge" : "rebase";
        if (!await Dialogs.ConfirmAsync($"Abort {what}", $"Abort the {what} and go back to how things were before it started? Conflict resolutions made so far are lost.", null, $"Abort {what}"))
            return;
        var wt = _state.WorkingDirectory;
        await RunGitAsync($"Aborting {what}…", () => op == RepoOperation.Merge ? GitActions.MergeAbortAsync(wt) : GitActions.RebaseAbortAsync(wt), $"Aborted the {what}.");
    }

    /// <summary>Merge and rebase entries for a branch, tag or commit menu.</summary>
    private IEnumerable<MenuAction> MergeRebaseActions(BranchTarget target, bool isCurrentTip)
    {
        if (_state?.CurrentBranch is not { } current || IsOperationInProgress || isCurrentTip) yield break;
        var label = target.Kind == RefKind.DetachedHead ? "this commit" : target.Name;
        yield return new MenuAction($"Merge {label} into {current}…", MergeCommand, target, Icon: MenuIcons.Merge);
        yield return new MenuAction($"Rebase {current} onto {label}…", RebaseOntoCommand, target, Icon: MenuIcons.Rebase);
    }

    // ------------------------------------------------------------------ stash

    [ObservableProperty]
    public partial bool HasStashes { get; set; }

    [RelayCommand]
    private async Task StashAsync()
    {
        if (_state is null || Dialogs is null) return;
        if (!_status.IsDirty)
        {
            ShowInfo("There are no changes to stash.");
            return;
        }
        var message = FormField.TextBox("Message (optional)", placeholder: "What these changes are");
        var untracked = FormField.CheckBox("Include untracked files", isChecked: _status.Unstaged.Any(f => f.Kind == ChangeKind.Untracked));
        if (!await Dialogs.ShowFormAsync(new FormSpec("Stash changes",
                "Saves your uncommitted changes and resets the working tree to the last commit.", "Stash", [message, untracked])))
            return;
        var wt = _state.WorkingDirectory;
        await RunGitAsync("Stashing…", () => GitActions.StashAsync(wt, message.Text, untracked.IsChecked), "Stashed your changes.");
    }

    /// <summary>Toolbar Pop: restores the newest stash.</summary>
    [RelayCommand]
    private Task PopLatestStashAsync() => _state?.Stashes.FirstOrDefault() is { } s ? PopStashAsync(s) : Task.CompletedTask;

    [RelayCommand]
    private Task ApplyStashAsync(StashInfo stash) => _state is null ? Task.CompletedTask
        : RunGitAsync("Applying stash…", () => GitActions.StashApplyAsync(_state.WorkingDirectory, stash.Index), $"Applied '{stash.Message}'. The stash is kept.");

    [RelayCommand]
    private Task PopStashAsync(StashInfo stash) => _state is null ? Task.CompletedTask
        : RunGitAsync("Popping stash…", () => GitActions.StashPopAsync(_state.WorkingDirectory, stash.Index), $"Restored '{stash.Message}'.");

    [RelayCommand]
    private async Task DropStashAsync(StashInfo stash)
    {
        if (_state is null || Dialogs is null) return;
        if (!await Dialogs.ConfirmAsync("Delete stash", $"Delete the stash '{stash.Message}'? Its changes will be lost.", null, "Delete"))
            return;
        if (SelectedSha == stash.Sha) SelectedSha = null;
        var wt = _state.WorkingDirectory;
        await RunGitAsync("Deleting stash…", () => GitActions.StashDropAsync(wt, stash.Index), "Deleted the stash.");
    }

    // ------------------------------------------------------------------ tags

    /// <summary>Creates a tag at the target's commit (a commit row, branch or tag).</summary>
    [RelayCommand]
    private async Task CreateTagAsync(BranchTarget target)
    {
        if (_state is null || Dialogs is null) return;
        var existing = _state.Refs.Where(r => r.Kind == RefKind.Tag).Select(r => r.Name).ToHashSet();
        var name = FormField.TextBox("Tag name", placeholder: "v1.0.0");
        var annotated = FormField.CheckBox("Annotated (with a message, tagger and date)", isChecked: true);
        var message = new FormField(FormFieldKind.MultilineText, "Message") { EnabledBy = annotated };
        var remote = await GitActions.DefaultRemoteAsync(_state.WorkingDirectory);
        var push = FormField.CheckBox($"Push the tag to {remote}");
        var fields = new List<FormField> { name, annotated, message };
        if (remote is not null) fields.Add(push);

        var where = target.Kind == RefKind.DetachedHead ? $"commit {target.Name}" : $"{target.Name} ({target.Sha[..Math.Min(7, target.Sha.Length)]})";
        var spec = new FormSpec("Create tag", $"Tags {where}.", "Create tag", fields, () =>
        {
            var n = name.Text.Trim();
            if (n.Length == 0) return "Enter a tag name.";
            if (!GitActions.IsValidRefName(n)) return "That isn't a valid tag name.";
            if (existing.Contains(n)) return $"A tag named '{n}' already exists.";
            if (annotated.IsChecked && string.IsNullOrWhiteSpace(message.Text)) return "Enter a message for the annotated tag.";
            return null;
        });
        if (!await Dialogs.ShowFormAsync(spec)) return;

        var tag = name.Text.Trim();
        var wt = _state.WorkingDirectory;
        var pushTo = push.IsChecked ? remote : null;
        await RunGitAsync($"Creating tag {tag}…", async () =>
        {
            await GitActions.CreateTagAsync(wt, tag, target.Sha, annotated.IsChecked ? message.Text : null);
            if (pushTo is not null) await GitActions.PushTagAsync(wt, pushTo, tag);
        }, pushTo is null ? $"Created tag {tag}." : $"Created tag {tag} and pushed it to {pushTo}.");
    }

    [RelayCommand]
    private async Task CheckoutCommitAsync(CommitInfo commit)
    {
        if (_state is null) return;
        var message = $"Check out {commit.ShortSha} \"{commit.MessageShort}\" without a branch (a \"detached HEAD\"), to look at, " +
                      "build or test it. If you commit here, create a branch (right-click the commit, Create branch here…) " +
                      "to keep the commits. To go back, check out a branch.";
        if (await AskLocalChangesAsync("Check out commit", message, "Check out", alwaysAsk: true) is not { } mode) return;

        var wt = _state.WorkingDirectory;
        await CheckoutWithChangesAsync(commit.ShortSha, mode, m => GitActions.CheckoutDetachedAsync(wt, commit.Sha, m),
            $"Checked out {commit.ShortSha} (detached HEAD). Create a branch here to keep any new commits.");
    }

    [RelayCommand]
    private Task CreateBranchAsync(BranchTarget target) => CreateBranchAtAsync(target);

    /// <summary>Toolbar and LOCAL section: a new branch from HEAD.</summary>
    [RelayCommand]
    private Task CreateBranchAtHeadAsync() => HeadTarget() is { } head ? CreateBranchAtAsync(head) : Task.CompletedTask;

    /// <summary>Right-click on a LOCAL folder: a new branch from HEAD, its name starting with the folder ("feature/").</summary>
    [RelayCommand]
    private Task CreateBranchInFolderAsync(string prefix) => HeadTarget() is { } head ? CreateBranchAtAsync(head, prefix) : Task.CompletedTask;

    private BranchTarget? HeadTarget() => _state is { HeadSha: { } sha } state
        ? state.CurrentBranch is { } branch
            ? new BranchTarget(RefKind.LocalBranch, branch, sha)
            : new BranchTarget(RefKind.DetachedHead, sha[..Math.Min(7, sha.Length)], sha)
        : null;

    private async Task CreateBranchAtAsync(BranchTarget target, string prefix = "")
    {
        if (_state is null || Dialogs is null) return;
        var existing = _state.Refs.Where(r => r.Kind == RefKind.LocalBranch).Select(r => r.Name).ToHashSet();
        // A branch made from bugs (or bug/x) starts "bug/", so it lands in the same group.
        if (prefix.Length == 0 && target.Kind is RefKind.LocalBranch or RefKind.RemoteBranch)
            prefix = BranchRuleSet.Current.PrefixForNewBranch(target.Kind == RefKind.RemoteBranch ? ShortRemote(target.Name) : target.Name);
        var name = FormField.TextBox("Branch name", prefix, placeholder: prefix.Length > 0 ? prefix + "my-change" : "feature/my-change",
            selectText: prefix.Length == 0);
        var checkout = FormField.CheckBox("Check it out", isChecked: true);

        var where = target.Kind == RefKind.DetachedHead ? $"commit {target.Name}" : $"{target.Name} ({target.Sha[..Math.Min(7, target.Sha.Length)]})";
        var spec = new FormSpec("Create branch", $"Creates a branch at {where}.", "Create branch", [name, checkout], () =>
        {
            var n = name.Text.Trim();
            if (n.Length == 0) return "Enter a branch name.";
            if (prefix.Length > 0 && n == prefix) return $"Enter a branch name after {prefix}";
            if (!GitActions.IsValidRefName(n)) return "That isn't a valid branch name.";
            if (existing.Contains(n)) return $"A branch named '{n}' already exists.";
            return null;
        });
        if (!await Dialogs.ShowFormAsync(spec)) return;

        var branch = name.Text.Trim();
        var wt = _state.WorkingDirectory;
        await RunGitAsync($"Creating branch {branch}…", () => GitActions.CreateBranchAsync(wt, branch, target.Sha, checkout.IsChecked),
            checkout.IsChecked ? $"Created and checked out {branch}." : $"Created branch {branch}.");
    }

    [RelayCommand]
    private async Task PushTagAsync(BranchTarget tag)
    {
        if (_state is null) return;
        var wt = _state.WorkingDirectory;
        var remote = await GitActions.DefaultRemoteAsync(wt);
        if (remote is null)
        {
            ShowError("This repository has no remotes to push to.");
            return;
        }
        await RunGitAsync($"Pushing {tag.Name}…", () => GitActions.PushTagAsync(wt, remote, tag.Name), $"Pushed tag {tag.Name} to {remote}.");
    }

    [RelayCommand]
    private async Task DeleteTagAsync(BranchTarget tag)
    {
        if (_state is null || Dialogs is null) return;
        if (!await Dialogs.ConfirmAsync("Delete tag", $"Delete the local tag '{tag.Name}'? It stays on any remote it was pushed to.", null, "Delete"))
            return;
        var wt = _state.WorkingDirectory;
        await RunGitAsync($"Deleting {tag.Name}…", () => GitActions.DeleteTagAsync(wt, tag.Name), $"Deleted tag {tag.Name}.");
    }

    [RelayCommand]
    private async Task DeleteRemoteTagAsync(BranchTarget tag)
    {
        if (_state is null || Dialogs is null) return;
        var wt = _state.WorkingDirectory;
        var remote = await GitActions.DefaultRemoteAsync(wt);
        if (remote is null) return;
        if (!await Dialogs.ConfirmAsync("Delete tag from remote", $"Delete the tag '{tag.Name}' from {remote}? The local tag is kept.", null, "Delete from remote"))
            return;
        await RunGitAsync($"Deleting {tag.Name} from {remote}…", () => GitActions.DeleteRemoteTagAsync(wt, remote, tag.Name),
            $"Deleted tag {tag.Name} from {remote}.");
    }
}
