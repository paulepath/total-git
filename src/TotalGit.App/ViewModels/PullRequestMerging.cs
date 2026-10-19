using TotalGit.Core.Git;
using TotalGit.Core.Hosting;

namespace TotalGit.App.ViewModels;

/// <summary>How the user chose to merge a pull request.</summary>
/// <param name="Title">The commit title, or null for the host's usual one.</param>
public sealed record MergeChoice(MergeMethod Method, string? Title, bool DeleteBranch);

/// <summary>
/// Merging a pull request, from its row's menu or its review window: asks how (saying first what may stop it),
/// merges on the host, then deletes its branch when asked.
/// </summary>
public static class PullRequestMerging
{
    /// <summary>Null when cancelled.</summary>
    public static async Task<MergeChoice?> AskAsync(PullRequestSummary pr, MergeOptions options, Func<FormSpec, Task<bool>> showForm)
    {
        if (options.Methods.Count == 0)
        {
            await showForm(new FormSpec($"Merge #{pr.Number}", "This repository has no merge method turned on for pull requests (see its settings on GitHub).", "OK", []));
            return null;
        }
        var problems = PullRequestTriage.MergeProblems(pr);
        var state = PullRequestTriage.IsReadyToMerge(pr) ? "Approved, checks passed, nothing in the way."
            : problems.Count > 0 ? $"GitHub may refuse: {string.Join(", ", problems)}."
            : "Nobody has approved it, but GitHub has nothing against merging it.";
        var message = $"{state} Merges {pr.HeadRef} into {pr.BaseRef} on GitHub.";

        var methods = options.Methods;
        var method = FormField.Choice("How", methods.Select(m => m switch
        {
            MergeMethod.Squash => new FormChoice("Squash and merge", $"All the changes as one commit on {pr.BaseRef}."),
            MergeMethod.Rebase => new FormChoice("Rebase and merge", $"Each commit replayed on top of {pr.BaseRef}, with no merge commit."),
            _ => new FormChoice("Create a merge commit", $"Keeps every commit, joined to {pr.BaseRef} by a merge commit."),
        }).ToList(), Math.Max(0, methods.ToList().IndexOf(options.Default)));
        var title = FormField.TextBox("Commit title (not used by a rebase)", placeholder: "Leave empty for GitHub's usual title");
        List<FormField> fields = [method, title];
        // A fork's branch isn't ours to delete, and some repositories delete merged branches themselves.
        var canDelete = !pr.IsCrossRepository && !options.DeletesBranchOnMerge;
        var delete = FormField.CheckBox($"Delete the branch {pr.HeadRef} afterwards", isChecked: true);
        if (canDelete) fields.Add(delete);

        if (!await showForm(new FormSpec($"Merge #{pr.Number}", message, "Merge", fields))) return null;
        return new MergeChoice(methods[method.SelectedIndex], string.IsNullOrWhiteSpace(title.Text) ? null : title.Text.Trim(), canDelete && delete.IsChecked);
    }

    /// <summary>
    /// Merges at the head the user saw (refused if someone pushed since), then deletes the branch if asked. Returns
    /// what to tell the user; a failed delete is reported there rather than thrown, as the merge itself is done.
    /// </summary>
    public static async Task<string> MergeAsync(IPullRequestProvider provider, PullRequestSummary pr, MergeChoice choice, string worktree)
    {
        await Task.Run(() => provider.MergeAsync(pr.Number, choice.Method, pr.HeadSha, choice.Title));
        var merged = $"Merged #{pr.Number} into {pr.BaseRef}.";
        if (!choice.DeleteBranch) return merged;
        try
        {
            await GitActions.DeleteRemoteBranchAsync(worktree, provider.Host.RemoteName, pr.HeadRef);
            return $"Merged #{pr.Number} into {pr.BaseRef} and deleted {pr.HeadRef}.";
        }
        catch (GitCommandException ex)
        {
            return $"{merged} Couldn't delete {pr.HeadRef}: {ex.Message}";
        }
    }
}
