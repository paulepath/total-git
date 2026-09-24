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

        // Ignore rules only matter for files git doesn't track yet, which are all in the unstaged list.
        if (!node.IsStaged)
        {
            if (node.IsFolder)
            {
                actions.Add(new MenuAction("Add folder to .gitignore…", AddToIgnoreCommand, "/" + GitIgnore.Escape(path)));
            }
            else
            {
                var name = node.File!.FileName;
                var ext = Path.GetExtension(name);
                var choices = new List<MenuAction> { new("This file…", AddToIgnoreCommand, "/" + GitIgnore.Escape(path)) };
                if (ext.Length > 1 && ext.Length < name.Length) choices.Add(new($"All {ext} files…", AddToIgnoreCommand, "*" + GitIgnore.Escape(ext)));
                choices.Add(new($"Files named {name}…", AddToIgnoreCommand, GitIgnore.Escape(name)));
                actions.Add(new MenuAction("Add to .gitignore", Children: choices));
            }
        }

        var folder = Path.Combine(_state.WorkingDirectory, node.FolderPath ?? node.File!.Change.Directory ?? "");
        actions.Add(MenuAction.Separator);
        actions.Add(new MenuAction("Copy path", CopyCommand, path));
        actions.Add(new MenuAction("Reveal folder", RevealCommand, folder));
        return actions;
    }

    /// <summary>Right-click in a diff: open the file at that line.</summary>
    public IReadOnlyList<MenuAction> ActionsForDiffLine(string path, int? line, int? column)
    {
        if (DiffFolder is not { } folder) return [];
        var exists = File.Exists(Path.Combine(folder, path));
        var actions = new List<MenuAction>();
        if (line is { } l && exists)
            actions.Add(new MenuAction($"Open in VS Code at line {l}", OpenFileInVsCodeCommand, new FileTarget(path, l, column, folder)));
        actions.Add(OpenInVsCodeAction(path, folder));
        actions.Add(MenuAction.Separator);
        actions.Add(new MenuAction("Copy path", CopyCommand, path));
        if (line is { } n) actions.Add(new MenuAction("Copy line number", CopyCommand, n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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
            new MenuAction("Copy path", CopyCommand, file.Path),
            new MenuAction("Reveal folder", RevealCommand, Path.Combine(folder, file.Change.Directory ?? "")),
        ];
    }

    private MenuAction OpenInVsCodeAction(string path, string? folder = null)
    {
        folder ??= _state?.WorkingDirectory;
        var exists = folder is not null && File.Exists(Path.Combine(folder, path));
        return new MenuAction(exists ? "Open in VS Code" : "Open in VS Code (file no longer exists)",
            OpenFileInVsCodeCommand, new FileTarget(path, Folder: folder), IsEnabled: exists);
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
