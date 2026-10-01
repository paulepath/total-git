using System.Windows.Input;
using TotalGit.Core.Git;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

/// <summary>A context-menu entry; views turn these into MenuItems so sidebar and graph share one menu.</summary>
/// <param name="Icon">A <see cref="MenuIcons"/> geometry key, or a bitmap (e.g. a branch-kind icon).</param>
public sealed record MenuAction(
    string Header,
    ICommand? Command = null,
    object? Parameter = null,
    IReadOnlyList<MenuAction>? Children = null,
    bool IsEnabled = true,
    object? Icon = null)
{
    public static MenuAction Separator { get; } = new("-");
    public bool IsSeparator => Header == "-";
}

/// <summary>Keys of the icon geometries in App.axaml used by context menus.</summary>
public static class MenuIcons
{
    public const string Branch = "BranchIcon";
    public const string Tag = "TagIcon";
    public const string Copy = "CopyIcon";
    public const string Delete = "DeleteIcon";
    public const string Checkout = "CheckoutIcon";
    public const string Rebase = "RebaseIcon";
    public const string Reset = "ResetIcon";
    public const string Open = "OpenIcon";
    public const string Merge = "MergeIcon";
    public const string Worktree = "WorktreeIcon";
    public const string Code = "CodeIcon";
    public const string VisualStudio = "VisualStudioIcon";
    public const string Folder = "FolderIcon";
    public const string Pop = "PopIcon";
    public const string Push = "PushIcon";
    public const string Stash = "StashIcon";
    public const string Plus = "PlusIcon";
    public const string Ignore = "IgnoreIcon";
    public const string Browser = "BrowserIcon";
    public const string PullRequest = "PullRequestIcon";
    public const string Refresh = "RefreshIcon";
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

/// <summary>A file (path relative to <c>Folder</c>, the tab's worktree when null), optionally at a line and column.</summary>
public sealed record FileTarget(string Path, int? Line = null, int? Column = null, string? Folder = null);

/// <summary>Message strip under the toolbar, with optional follow-up actions.</summary>
public sealed record Banner(string Message, bool IsError, IReadOnlyList<MenuAction> Actions)
{
    public bool HasActions => Actions.Count > 0;
}

/// <summary>One answer in a choice dialog.</summary>
/// <param name="IsPrimary">The highlighted, default answer.</param>
/// <param name="IsDanger">Destroys something (shown in red).</param>
public sealed record DialogChoice(string Text, bool IsPrimary = false, bool IsDanger = false, string? ToolTip = null);

public interface IDialogService
{
    Task<string?> PickFolderAsync();
    Task<bool> ConfirmAsync(string title, string message, IReadOnlyList<string>? details = null, string confirmText = "OK");

    /// <summary>Asks a question with several answers; the index of the chosen one, or null when cancelled.</summary>
    Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string>? details, IReadOnlyList<DialogChoice> choices);
    Task<bool> ShowCreateWorktreeAsync(CreateWorktreeViewModel viewModel);
    Task<bool> ShowFormAsync(FormSpec spec);
    Task<bool> ShowInteractiveRebaseAsync(InteractiveRebaseViewModel viewModel);
    Task<bool> ShowAddIgnoreAsync(AddIgnoreViewModel viewModel);
    Task<bool> ShowBranchCleanupAsync(BranchCleanupViewModel viewModel);
    Task<bool> ShowRebaseCommitsAsync(RebaseCommitsViewModel viewModel);
    Task<bool> ShowBranchRulesAsync(BranchRulesViewModel viewModel);
    Task CopyToClipboardAsync(string text);
    Task RevealFolderAsync(string path);
}
