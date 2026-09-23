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
}
