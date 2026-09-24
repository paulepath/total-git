using TotalGit.Core.Git;

namespace TotalGit.App.ViewModels;

// Checking out a branch or commit with uncommitted changes: keep, merge, stash or discard them.
public sealed partial class RepositoryViewModel
{
    private static readonly FormChoice[] LocalChangeChoices =
    [
        new("Don't change", "Bring your changes along. Git stops if a changed file is also different on the other side."),
        new("Merge", "Bring your changes along, merging them into files that differ. May leave conflicts to resolve."),
        new("Stash", "Put your changes in the stash list, so you start clean. Pop the stash later to get them back."),
        new("Discard", "Throw away your changes to tracked files (new, untracked files are kept). This can't be undone.", IsWarning: true),
    ];

    private static readonly FormChoice[] ResetChoices =
    [
        new("Soft", "Move the branch only. Your files and staged changes stay as they are; the undone commits' changes show as staged."),
        new("Mixed", "Move the branch and unstage everything. Your files stay as they are; the undone commits' changes show as unstaged."),
        new("Keep", "Move the branch and update your files to that commit. Stops if that would touch files you've changed."),
        new("Merge", "Move the branch and update your files, keeping your uncommitted changes. Stops if they conflict."),
        new("Hard", "Move the branch and make every tracked file match that commit. ALL uncommitted changes are lost.", IsWarning: true),
    ];

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task ResetToCommitAsync(CommitInfo commit)
    {
        if (_state is not { } state || Dialogs is null) return;
        var wt = state.WorkingDirectory;
        var branch = state.CurrentBranch ?? "HEAD";
        var previous = state.HeadSha;

        var (removed, pushed) = await GitActions.ResetImpactAsync(wt, commit.Sha);
        var refs = Graph?.Refs.Where(r => r.TargetSha == commit.Sha && r.Kind is RefKind.LocalBranch or RefKind.RemoteBranch or RefKind.Tag)
            .Select(r => r.Name).ToList() ?? [];
        var lines = new List<string>
        {
            $"Move {branch} to {commit.ShortSha} \"{commit.MessageShort}\"",
            $"by {commit.AuthorName}, {commit.AuthorDate.LocalDateTime:g}{(refs.Count > 0 ? $" ({string.Join(", ", refs)})" : "")}.",
        };
        if (removed > 0)
        {
            lines.Add("");
            lines.Add($"{removed} commit{(removed == 1 ? "" : "s")} after it will come off the branch (they can still be found in git's reflog).");
            if (pushed > 0)
                lines.Add($"{pushed} of them {(pushed == 1 ? "is" : "are")} already pushed, so the branch would need a force push afterwards.");
        }

        var mode = FormField.Choice("Reset type", ResetChoices);
        if (!await Dialogs.ShowFormAsync(new FormSpec($"Reset {branch}", string.Join('\n', lines), "Reset", [mode]))) return;

        var reset = (ResetMode)mode.SelectedIndex;
        var ok = await RunGitAsync($"Resetting {branch}…", () => GitActions.ResetAsync(wt, commit.Sha, reset));
        if (ok && previous is not null)
        {
            Banner = new Banner($"Reset {branch} to {commit.ShortSha} ({reset.ToString().ToLowerInvariant()}). It was at {previous[..7]}.", false,
                [new MenuAction("Copy previous SHA", CopyCommand, previous)]);
        }
    }

    /// <summary>Changes that a checkout would have to deal with (untracked files alone don't count).</summary>
    private bool HasLocalChanges => _status.Staged.Count > 0 || _status.Unstaged.Any(f => f.Kind != ChangeKind.Untracked);

    /// <summary>
    /// Asks what to do with uncommitted changes (null when cancelled). Without changes there is nothing to ask,
    /// unless <paramref name="alwaysAsk"/> (the dialog also confirms the checkout).
    /// </summary>
    private async Task<LocalChanges?> AskLocalChangesAsync(string title, string message, string confirmText, bool alwaysAsk = false)
    {
        if (!HasLocalChanges && !alwaysAsk) return LocalChanges.Keep;
        if (Dialogs is null) return null;

        var fields = new List<FormField>();
        FormField? choice = null, remember = null;
        if (HasLocalChanges)
        {
            var count = _status.Staged.Select(f => f.Path).Concat(_status.Unstaged.Where(f => f.Kind != ChangeKind.Untracked).Select(f => f.Path)).Distinct().Count();
            choice = FormField.Choice($"Local changes ({count} file{(count == 1 ? "" : "s")})", LocalChangeChoices, (int)_settings.CheckoutLocalChanges);
            remember = FormField.CheckBox("Remember this choice");
            fields.Add(choice);
            fields.Add(remember);
        }
        if (!await Dialogs.ShowFormAsync(new FormSpec(title, message, confirmText, fields))) return null;
        if (choice is null) return LocalChanges.Keep;

        var mode = (LocalChanges)choice.SelectedIndex;
        if (remember!.IsChecked)
        {
            _settings.CheckoutLocalChanges = mode;
            _settings.Save();
        }
        return mode;
    }

    /// <summary>Runs a checkout, stashing first when asked to; a stash is reported with a way to get it back.</summary>
    private async Task CheckoutWithChangesAsync(string what, LocalChanges mode, Func<LocalChanges, Task> checkout, string? success = null)
    {
        var wt = _state!.WorkingDirectory;
        if (mode == LocalChanges.Stash
            && !await RunGitAsync("Stashing your changes…", () => GitActions.StashAsync(wt, $"Before checking out {what}", includeUntracked: false), refresh: Refresh.None))
            return;

        // Always report the checkout, so an older banner (e.g. from a stash) doesn't linger.
        success ??= $"Checked out {what}.";
        var ok = await RunGitAsync($"Checking out {what}…", () => checkout(mode), success);
        if (mode != LocalChanges.Stash) return;

        var pop = new MenuAction("Pop stash", PopLatestStashCommand);
        Banner = ok
            ? new Banner($"{success} Your changes are in the stash list.", false, [pop])
            : new Banner($"{Banner?.Message}\nYour changes are saved in the stash list.", true, [pop]);
    }
}
