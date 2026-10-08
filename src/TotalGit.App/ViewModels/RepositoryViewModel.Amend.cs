using TotalGit.Core.Git;

namespace TotalGit.App.ViewModels;

public sealed partial class RepositoryViewModel
{
    private string? _amendHeadSha;
    public bool CanAmendHead => _state is { HeadSha: not null, Operation: RepoOperation.None };

    private async Task<string?> LoadAmendHeadAsync()
    {
        if (!CanAmendHead) return null;
        string? message = null;
        var worktree = _state!.WorkingDirectory;
        var head = _state.HeadSha;
        _amendHeadSha = null;
        await RunGitAsync("Reading last commit…", async () =>
        {
            var text = await GitActions.HeadMessageAsync(worktree);
            var current = (await GitCli.RunAsync(worktree, "rev-parse", "HEAD")).StdOut.Trim();
            if (current != head) throw new InvalidOperationException("HEAD changed. Refresh before loading the last commit.");
            message = text;
            _amendHeadSha = head;
        }, refresh: Refresh.None);
        return message;
    }

    private async Task<bool> AmendHeadAsync(string message)
    {
        if (!CanAmendHead || Dialogs is null) return false;
        var worktree = _state!.WorkingDirectory;
        var head = _amendHeadSha;
        var amended = false;
        var ok = await RunGitAsync("Amending…", async () =>
        {
            if (await GitActions.HeadIsPushedAsync(worktree)
                && !await Dialogs.ConfirmAsync("Amend a pushed commit?",
                    "This commit is on a fetched remote branch. Amending rewrites it; publishing the replacement may require a force push.", null, "Amend"))
                return;
            var current = (await GitCli.RunAsync(worktree, "rev-parse", "HEAD")).StdOut.Trim();
            if (current != head) throw new InvalidOperationException("HEAD changed. Reload the last commit before amending.");
            await GitActions.AmendAsync(worktree, message);
            amended = true;
        });
        return ok && amended;
    }
}
