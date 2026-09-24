using System.Windows.Input;
using TotalGit.Core.Git;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

/// <summary>A context-menu entry; views turn these into MenuItems so sidebar and graph share one menu.</summary>
public sealed record MenuAction(
    string Header,
    ICommand? Command = null,
    object? Parameter = null,
    IReadOnlyList<MenuAction>? Children = null,
    bool IsEnabled = true)
{
    public static MenuAction Separator { get; } = new("-");
    public bool IsSeparator => Header == "-";
}

/// <summary>A branch, tag or worktree that actions can target.</summary>
public sealed record BranchTarget(
    RefKind Kind,
    string Name,
    string Sha,
    string? RemoteName = null,
    WorktreeInfo? Worktree = null)
{
    /// <summary>Local branch name (remote prefix removed).</summary>
    public string ShortName => RemoteName is not null && Name.StartsWith(RemoteName + "/", StringComparison.Ordinal)
        ? Name[(RemoteName.Length + 1)..]
        : Name;

    public static BranchTarget From(RefInfo r, WorktreeInfo? worktree) =>
        new(r.Kind, r.Name, r.TargetSha, r.RemoteName, worktree);
}

/// <summary>A file in the worktree (repository-relative path), optionally at a line and column.</summary>
public sealed record FileTarget(string Path, int? Line = null, int? Column = null);

/// <summary>Message strip under the toolbar, with optional follow-up actions.</summary>
public sealed record Banner(string Message, bool IsError, IReadOnlyList<MenuAction> Actions)
{
    public bool HasActions => Actions.Count > 0;
}

public interface IDialogService
{
    Task<string?> PickFolderAsync();
    Task<bool> ConfirmAsync(string title, string message, IReadOnlyList<string>? details = null, string confirmText = "OK");
    Task<bool> ShowCreateWorktreeAsync(CreateWorktreeViewModel viewModel);
    Task<bool> ShowFormAsync(FormSpec spec);
    Task<bool> ShowInteractiveRebaseAsync(InteractiveRebaseViewModel viewModel);
    Task<bool> ShowAddIgnoreAsync(AddIgnoreViewModel viewModel);
    Task CopyToClipboardAsync(string text);
    Task RevealFolderAsync(string path);
}
