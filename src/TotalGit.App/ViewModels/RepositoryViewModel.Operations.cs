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
