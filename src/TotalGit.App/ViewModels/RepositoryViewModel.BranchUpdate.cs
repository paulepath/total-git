using CommunityToolkit.Mvvm.Input;
using TotalGit.Core.Git;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

// Double-clicking a branch's ↓N in the sidebar: bring the remote branch's new commits into the local branch.
public partial class RepositoryViewModel
{
    [RelayCommand]
    private async Task UpdateBranchFromRemoteAsync(BranchTarget target)
    {
        if (_state is null || target.Kind != RefKind.LocalBranch) return;
        var branch = _state.Refs.FirstOrDefault(r => r.Kind == RefKind.LocalBranch && r.Name == target.Name);
        var upstream = branch?.Upstream is { } u ? _state.Refs.FirstOrDefault(r => r.Kind == RefKind.RemoteBranch && r.Name == u) : null;
        if (branch is null || upstream?.RemoteName is not { } remote)
        {
            ShowError($"{target.Name} doesn't track a remote branch, so there's nothing to bring in.");
            return;
        }

        var name = branch.Name;
        var commits = branch.Behind == 1 ? "1 commit" : $"{branch.Behind} commits";
        var here = _state.WorkingDirectory;
        var worktree = _worktrees.FirstOrDefault(w => w.Branch == name);

        // The branch checked out here: the same as the toolbar's Pull (which also handles a diverged branch).
        if (worktree is not null && WorktreeService.SamePath(worktree.Path, here))
        {
            await PullAsync();
            return;
        }

        if (branch.Ahead > 0)
        {
            var own = branch.Ahead == 1 ? "1 commit" : $"{branch.Ahead} commits";
            var next = worktree is null ? "Check it out and pull to combine them." : "Open its worktree and pull to combine them.";
            Banner = new Banner($"{name} has {own} of its own and {commits} new on {upstream.Name}, so it can't simply move forward. {next}", false,
                worktree is null ? [new MenuAction("Check out", CheckoutCommand, target)] : WorktreeOpenActions(worktree));
            return;
        }

        // Checked out in another worktree: pull there (fast-forward only, so nothing is merged unexpectedly).
        var ok = worktree is not null
            ? await RunGitAsync($"Updating {name}…", () => GitActions.PullFastForwardAsync(worktree.Path))
            : await RunGitAsync($"Updating {name}…", () => GitActions.FastForwardBranchAsync(here, remote, upstream.ShortName, name));
        if (ok) ShowInfo($"Updated {name} with {commits} from {upstream.Name}.");
    }

    /// <summary>
    /// The branch this worktree already has checked out, or a remote branch it tracks: checking it out again
    /// would do nothing, so double-clicking it doesn't try.
    /// </summary>
    public bool IsCheckedOutHere(BranchTarget target) =>
        _state?.Refs.FirstOrDefault(r => r is { Kind: RefKind.LocalBranch, IsCurrent: true }) is { } current
        && (target.Kind == RefKind.LocalBranch ? target.Name == current.Name : target.Name == current.Upstream);

    /// <summary>Double-clicking a branch's ↑N: push its new commits to the remote branch it tracks.</summary>
    [RelayCommand]
    private async Task PushBranchToRemoteAsync(BranchTarget target)
    {
        if (_state is null || target.Kind != RefKind.LocalBranch) return;
        var branch = _state.Refs.FirstOrDefault(r => r.Kind == RefKind.LocalBranch && r.Name == target.Name);
        var upstream = branch?.Upstream is { } u ? _state.Refs.FirstOrDefault(r => r.Kind == RefKind.RemoteBranch && r.Name == u) : null;
        if (branch is null || upstream?.RemoteName is not { } remote)
        {
            ShowError($"{target.Name} doesn't track a remote branch. Check it out and push to create one.");
            return;
        }

        // The branch checked out here: the same as the toolbar's Push (which also handles a rejected push).
        if (branch.IsCurrent)
        {
            await PushAsync();
            return;
        }

        var name = branch.Name;
        if (branch.Behind > 0)
        {
            var theirs = branch.Behind == 1 ? "1 commit" : $"{branch.Behind} commits";
            Banner = new Banner($"{upstream.Name} has {theirs} that {name} doesn't, so pushing would be refused. Bring them in first " +
                                "(double-click the ↓), or check the branch out to force push.", false, [new MenuAction("Check out", CheckoutCommand, target)]);
            return;
        }

        var commits = branch.Ahead == 1 ? "1 commit" : $"{branch.Ahead} commits";
        var here = _state.WorkingDirectory;
        if (await RunGitAsync($"Pushing {name}…", async () =>
            {
                try
                {
                    await GitActions.PushBranchAsync(here, remote, name, upstream.ShortName);
                }
                catch (GitCommandException ex) when (GitActions.ClassifyPushError(ex.Message) is not null)
                {
                    // Not the current branch, so the usual pull-or-force-push choice doesn't apply.
                    throw new InvalidOperationException($"{upstream.Name} has commits {name} doesn't (pushed since the last fetch). Fetch, bring them in, then push.");
                }
            }, $"Pushed {commits} from {name} to {upstream.Name}."))
            CheckWorkflowsSoon();
    }
}
