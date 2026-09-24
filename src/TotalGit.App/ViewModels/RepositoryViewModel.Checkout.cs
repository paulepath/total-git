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
