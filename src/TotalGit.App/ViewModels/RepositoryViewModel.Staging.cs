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
        var actions = new List<MenuAction> { new(node.IsFolder ? $"{node.ActionText} folder" : node.ActionText, staging.StageNodeCommand, node) };

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
