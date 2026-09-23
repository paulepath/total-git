using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.Core.Git;

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

    /// <summary>Local branches whose remote branch was deleted and which aren't checked out anywhere.</summary>
    private List<RefInfo> GoneBranches() => _state?.Refs
        .Where(r => r is { Kind: RefKind.LocalBranch, UpstreamGone: true, IsCurrent: false }
                    && !_worktrees.Any(w => w.Branch == r.Name))
        .ToList() ?? [];

    [RelayCommand]
    private async Task DeleteGoneBranchesAsync()
    {
        if (_state is null || Dialogs is null) return;
        var gone = GoneBranches();
        if (gone.Count == 0) return;
        if (!await Dialogs.ConfirmAsync("Delete branches",
                "These local branches track remote branches that were deleted. Delete them (including any unmerged commits)?",
                gone.Select(r => $"{r.Name}  ({r.Upstream})").ToList(), $"Delete {gone.Count}"))
            return;

        var wt = _state.WorkingDirectory;
        await RunGitAsync("Deleting branches…", async () =>
        {
            foreach (var r in gone) await GitActions.DeleteBranchAsync(wt, r.Name, force: true);
        }, $"Deleted {gone.Count} branch{(gone.Count == 1 ? "" : "es")}.");
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

    private void OpenMergeTool(string path)
    {
        if (_state is null) return;
        var wt = _state.WorkingDirectory;
        try
        {
            MergeTool = new MergeToolViewModel(wt, path,
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
                confirm: (title, message) => Dialogs?.ConfirmAsync(title, message, null, "Save anyway") ?? Task.FromResult(false),
                close: () =>
                {
                    if (Staging is not null) Staging.SelectedFile = null;
                    MergeTool = null;
                });
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
        yield return new MenuAction($"Merge {label} into {current}…", MergeCommand, target);
        yield return new MenuAction($"Rebase {current} onto {label}…", RebaseOntoCommand, target);
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
