using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.Core.Git;

namespace TotalGit.App.ViewModels;

// Staging panel: right-click actions on files and folders, and ignore rules.
public sealed partial class RepositoryViewModel
{
    public IReadOnlyList<MenuAction> ActionsForStagingNode(StagingNode node)
    {
        if (Staging is not { } staging || _state is null) return [];

        var path = node.FolderPath ?? node.File!.Path;
        var actions = new List<MenuAction>();
        if (node.File is { } file)
        {
            actions.Add(OpenInVsCodeAction(file.Path));
            actions.Add(MenuAction.Separator);
        }
        actions.Add(new MenuAction(node.IsFolder ? $"{node.ActionText} folder" : node.ActionText, staging.StageNodeCommand, node));
        var discardable = node.Files().Any(f => f.Change.Kind != ChangeKind.Conflicted);
        var discardText = node.IsStaged ? "Discard all changes to " + (node.IsFolder ? "folder…" : "file…")
            : node.IsFolder ? "Discard changes in folder…" : "Discard changes…";
        actions.Add(new MenuAction(discardable ? discardText : "Discard changes (resolve the conflict instead)", DiscardNodeCommand, node,
            IsEnabled: discardable && !IsBusy, Icon: MenuIcons.Reset));

        // Ignore rules only matter for files git doesn't track yet, which are all in the unstaged list.
        if (!node.IsStaged)
        {
            if (node.IsFolder)
            {
                actions.Add(new MenuAction("Add folder to .gitignore…", AddToIgnoreCommand, "/" + GitIgnore.Escape(path), Icon: MenuIcons.Ignore));
            }
            else
            {
                var name = node.File!.FileName;
                var ext = Path.GetExtension(name);
                var choices = new List<MenuAction> { new("This file…", AddToIgnoreCommand, "/" + GitIgnore.Escape(path), Icon: MenuIcons.Ignore) };
                if (ext.Length > 1 && ext.Length < name.Length) choices.Add(new($"All {ext} files…", AddToIgnoreCommand, "*" + GitIgnore.Escape(ext), Icon: MenuIcons.Ignore));
                choices.Add(new($"Files named {name}…", AddToIgnoreCommand, GitIgnore.Escape(name), Icon: MenuIcons.Ignore));
                actions.Add(new MenuAction("Add to .gitignore", Children: choices, Icon: MenuIcons.Ignore));
            }
        }

        var folder = Path.Combine(_state.WorkingDirectory, node.FolderPath ?? node.File!.Change.Directory ?? "");
        actions.Add(MenuAction.Separator);
        actions.Add(new MenuAction("Copy path", CopyCommand, path, Icon: MenuIcons.Copy));
        actions.Add(new MenuAction("Reveal folder", RevealCommand, folder, Icon: MenuIcons.Folder));
        return actions;
    }

    /// <summary>Right-click in a diff: open the file at that line.</summary>
    public IReadOnlyList<MenuAction> ActionsForDiffLine(string path, int? line, int? column)
    {
        if (DiffFolder is not { } folder) return [];
        var exists = File.Exists(Path.Combine(folder, path));
        var actions = new List<MenuAction>();
        if (line is { } l && exists)
            actions.Add(new MenuAction($"Open in VS Code at line {l}", OpenFileInVsCodeCommand, new FileTarget(path, l, column, folder), Icon: MenuIcons.Code));
        actions.Add(OpenInVsCodeAction(path, folder));
        actions.Add(MenuAction.Separator);
        actions.Add(new MenuAction("Copy path", CopyCommand, path, Icon: MenuIcons.Copy));
        if (line is { } n) actions.Add(new MenuAction("Copy line number", CopyCommand, n.ToString(System.Globalization.CultureInfo.InvariantCulture), Icon: MenuIcons.Copy));
        return actions;
    }

    /// <summary>Right-click on a file in a commit's (or another worktree's) file list.</summary>
    public IReadOnlyList<MenuAction> ActionsForFile(FileChangeItem file, string? folder = null)
    {
        folder ??= _state?.WorkingDirectory;
        if (folder is null) return [];
        return
        [
            OpenInVsCodeAction(file.Path, folder),
            MenuAction.Separator,
            new MenuAction("Copy path", CopyCommand, file.Path, Icon: MenuIcons.Copy),
            new MenuAction("Reveal folder", RevealCommand, Path.Combine(folder, file.Change.Directory ?? ""), Icon: MenuIcons.Folder),
        ];
    }

    private MenuAction OpenInVsCodeAction(string path, string? folder = null)
    {
        folder ??= _state?.WorkingDirectory;
        var exists = folder is not null && File.Exists(Path.Combine(folder, path));
        return new MenuAction(exists ? "Open in VS Code" : "Open in VS Code (file no longer exists)",
            OpenFileInVsCodeCommand, new FileTarget(path, Folder: folder), IsEnabled: exists, Icon: MenuIcons.Code);
    }

    /// <summary>Right-click Discard on a file or folder in the staging lists, after a confirmation.</summary>
    [RelayCommand]
    private async Task DiscardNodeAsync(StagingNode node)
    {
        if (Dialogs is null || _state is not { } state) return;
        // The files themselves, not the folder path: a filtered list only shows (and discards) some of a folder.
        var files = node.Files().Select(f => f.Change).Where(f => f.Kind != ChangeKind.Conflicted).ToList();
        if (files.Count == 0) return;

        var untracked = files.Where(f => f.Kind == ChangeKind.Untracked).Select(f => f.Path).ToList();
        var restored = files.Where(f => f.Kind != ChangeKind.Untracked).Select(f => f.Path).ToList();
        var details = new List<string>();
        if (restored.Count > 0)
        {
            details.Add(node.IsStaged ? "Back to the last commit (staged and unstaged changes):" : "Back to the staged version:");
            details.AddRange(restored.Select(p => "  " + p));
        }
        if (untracked.Count > 0)
        {
            details.Add("Deleted (never committed):");
            details.AddRange(untracked.Select(p => "  " + p));
        }
        var what = files.Count == 1 ? files[0].Path : $"{files.Count} files";
        var message = node.IsStaged
            ? $"Discard all changes to {what}, staged and unstaged? A backup will be kept for Undo for up to seven days."
            : restored.Count == 0
                ? $"Delete {what}? These untracked files will be backed up for Undo for up to seven days."
                : $"Discard the unstaged changes to {what}? Anything already staged is kept. A backup will be kept for Undo for up to seven days.";
        if (!await Dialogs.ConfirmAsync("Discard changes", message, details, "Discard")) return;

        var wt = state.WorkingDirectory;
        await RunDiscardAsync(
            () => node.IsStaged ? GitActions.DiscardStagedAsync(wt, files) : GitActions.DiscardUnstagedAsync(wt, files),
            files.Count == 1 ? $"Discarded the changes to {files[0].Path}." : $"Discarded the changes to {files.Count} files.");
    }

    /// <summary>WIP row: throw away every uncommitted change (untracked files only if asked).</summary>
    [RelayCommand]
    private async Task DiscardAllAsync()
    {
        if (Dialogs is null || _state is not { } state || !_status.IsDirty || IsOperationInProgress) return;
        var tracked = _status.Staged.Select(f => f.Path)
            .Concat(_status.Unstaged.Where(f => f.Kind != ChangeKind.Untracked).Select(f => f.Path)).Distinct().ToList();
        var untrackedCount = _status.Unstaged.Count(f => f.Kind == ChangeKind.Untracked);
        var fields = new List<FormField>();
        var untracked = FormField.CheckBox($"Also delete {untrackedCount} untracked file{(untrackedCount == 1 ? "" : "s")} (never committed)");
        if (untrackedCount > 0) fields.Add(untracked);
        var description = tracked.Count > 0
            ? $"Throws away all uncommitted changes to {tracked.Count} tracked file{(tracked.Count == 1 ? "" : "s")}, staged and unstaged, " +
              "putting them back as they were at the last commit. A backup will be kept for Undo for up to seven days."
            : "There are only untracked files. Tick the box to delete them. A backup will be kept for Undo for up to seven days.";
        string? Validate() => tracked.Count == 0 && !untracked.IsChecked ? "Nothing to discard unless the untracked files are deleted." : null;
        if (!await Dialogs.ShowFormAsync(new FormSpec("Discard all changes", description, "Discard", fields, Validate))) return;

        var wt = state.WorkingDirectory;
        var includeUntracked = untracked.IsChecked;
        await RunDiscardAsync(() => GitActions.DiscardAllAsync(wt, includeUntracked),
            includeUntracked ? "Discarded all changes and deleted the untracked files." : "Discarded all changes.");
    }

    private async Task RunDiscardAsync(Func<Task<DiscardBackup?>> discard, string success)
    {
        DiscardBackup? backup = null;
        var ok = await RunGitAsync("Discarding…", async () => backup = await discard(), refresh: Refresh.Status);
        if (ok && backup is not null)
            Banner = new Banner(success + " Backup kept for up to seven days (last 20 discards).", false,
                [new MenuAction("Undo", UndoDiscardCommand, backup)]);
        await UpdateCanUndoDiscardAsync();
    }

    /// <summary>Whether there's a discard backup to restore (the toolbar's Undo discard button).</summary>
    [ObservableProperty]
    public partial bool CanUndoDiscard { get; private set; }

    private async Task UpdateCanUndoDiscardAsync()
    {
        var wt = _state?.WorkingDirectory;
        try { CanUndoDiscard = wt is not null && await DiscardBackup.LatestAsync(wt) is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GitCommandException) { CanUndoDiscard = false; }
    }

    [RelayCommand]
    private async Task UndoDiscardAsync(DiscardBackup? backup)
    {
        if (_state is null || IsBusy || IsOperationInProgress) return;
        var wt = _state.WorkingDirectory;
        await RunGitAsync("Restoring discarded changes…", async () =>
        {
            backup ??= await DiscardBackup.LatestAsync(wt);
            if (backup is null) throw new InvalidOperationException("No discard backup is available (backups last seven days, up to 20 discards).");
            await backup.UndoAsync(wt);
        }, "Restored discarded changes and their staging state.", Refresh.Status);
        await UpdateCanUndoDiscardAsync();
    }

    /// <summary>Offers Undo for what a checkout or hard reset just discarded (only a backup it made, not an older one).</summary>
    private async Task OfferDiscardUndoAsync(string message, DateTimeOffset since)
    {
        if (_state is null) return;
        await UpdateCanUndoDiscardAsync();
        var backup = await DiscardBackup.LatestAsync(_state.WorkingDirectory);
        if (backup is not null && backup.Created >= since)
            Banner = new Banner(message + " Discarded files are backed up for up to seven days.", false,
                [new MenuAction("Undo discarded files", UndoDiscardCommand, backup)]);
    }

    [RelayCommand]
    private async Task AddToIgnoreAsync(string rule)
    {
        if (Dialogs is null || _state is not { } state) return;
        var dialog = new AddIgnoreViewModel(state.WorkingDirectory, rule);
        if (!await Dialogs.ShowAddIgnoreAsync(dialog)) return;
        await RunGitAsync("Adding ignore rules…",
            () => GitActions.AddIgnoreRulesAsync(state.WorkingDirectory, dialog.Rules, dialog.Target), refresh: Refresh.Status);
    }
}
