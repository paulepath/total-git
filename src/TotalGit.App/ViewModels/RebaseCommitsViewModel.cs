using CommunityToolkit.Mvvm.ComponentModel;
using TotalGit.Core.Git;

namespace TotalGit.App.ViewModels;

/// <summary>A commit in a picked range, for lists.</summary>
public sealed class RangeCommitItem(CommitInfo commit)
{
    public CommitInfo Commit => commit;
    public string ShortSha => commit.ShortSha;
    public string Subject => commit.MessageShort;
    public string Author => commit.AuthorName;
    public string Date => commit.AuthorDate.LocalDateTime.ToString("g");
}

/// <summary>A local branch the replayed commits could go to, and where it's checked out.</summary>
public sealed record BranchChoice(string Name, bool HasUpstream, string? OtherWorktreePath);

/// <summary>
/// The "Rebase selected commits" dialog: the commits, the branch to replay them onto, and whether an existing
/// branch (one whose tip is the newest selected commit) moves or a new branch gets them.
/// </summary>
public sealed partial class RebaseCommitsViewModel : ObservableObject
{
    private readonly IReadOnlyCollection<string> _localBranches;
    private readonly Func<string, string?> _describeTarget;
    private readonly Func<string, string, Task<IReadOnlyList<string>>> _droppedCommits;
    private readonly string? _currentBranch;
    private int _previewRequest;

    /// <param name="commits">Newest first.</param>
    /// <param name="tipBranches">Local branches whose tip is the newest selected commit.</param>
    /// <param name="targets">Branches to replay onto: local ones first, then remote ones.</param>
    /// <param name="sourceBranch">A branch the commits are on, for naming a new branch.</param>
    /// <param name="describeTarget">A target's tip, e.g. "a1b2c3d Fix the build", or null when it doesn't exist.</param>
    /// <param name="droppedCommits">(target, moved branch) → commits the moved branch loses: those below the selection that aren't in the target.</param>
    public RebaseCommitsViewModel(IReadOnlyList<CommitInfo> commits, IReadOnlyList<BranchChoice> tipBranches, IReadOnlyList<string> targets,
        string defaultTarget, IReadOnlyCollection<string> localBranches, string? currentBranch, string? sourceBranch,
        Func<string, string?> describeTarget, Func<string, string, Task<IReadOnlyList<string>>> droppedCommits)
    {
        Commits = commits.Select(c => new RangeCommitItem(c)).ToList();
        TipBranches = tipBranches;
        Targets = targets;
        _localBranches = localBranches;
        _currentBranch = currentBranch;
        _describeTarget = describeTarget;
        _droppedCommits = droppedCommits;

        BranchToMove = tipBranches.FirstOrDefault(b => b.Name == currentBranch) ?? tipBranches.FirstOrDefault();
        MoveExisting = BranchToMove is not null;
        NewBranchName = UniqueName($"{BranchToMove?.Name ?? sourceBranch ?? "commits"}-rebased");
        Target = defaultTarget;
    }

    public IReadOnlyList<RangeCommitItem> Commits { get; }
    public IReadOnlyList<BranchChoice> TipBranches { get; }
    public IReadOnlyList<string> Targets { get; }

    public string Heading => Commits.Count == 1 ? "Rebase 1 commit" : $"Rebase {Commits.Count} commits";
    public bool CanMoveExisting => TipBranches.Count > 0;
    public bool HasSeveralTipBranches => TipBranches.Count > 1;
    public string MoveExistingText => BranchToMove is { } b ? $"Move {b.Name} (its tip is the newest selected commit)" : "Move the branch";

    /// <summary>Only commits below a branch's tip were picked, so they can only go to a new branch.</summary>
    public string? NotATipNote => CanMoveExisting ? null
        : "The newest selected commit isn't the tip of a branch, so the commits are replayed into a new branch and the original branch is left as it is.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetDescription), nameof(Error), nameof(IsValid))]
    public partial string Target { get; set; } = "";

    public string? TargetDescription => _describeTarget(Target.Trim()) is { } d ? $"Tip: {d}" : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveNew), nameof(Error), nameof(IsValid))]
    public partial bool MoveExisting { get; set; }

    public bool MoveNew
    {
        get => !MoveExisting;
        set => MoveExisting = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveExistingText), nameof(Error), nameof(IsValid))]
    public partial BranchChoice? BranchToMove { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(IsValid))]
    public partial string NewBranchName { get; set; } = "";

    /// <summary>The branch that ends up with the replayed commits.</summary>
    public string ResultBranch => MoveExisting && BranchToMove is { } b ? b.Name : NewBranchName.Trim();

    /// <summary>What will happen, in words; refreshed as the choices change.</summary>
    [ObservableProperty]
    public partial string Preview { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<string> Warnings { get; private set; } = [];

    public bool HasWarnings => Warnings.Count > 0;

    partial void OnWarningsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasWarnings));

    public string? Error
    {
        get
        {
            var target = Target.Trim();
            if (target.Length == 0) return "Choose the branch to rebase onto.";
            if (_describeTarget(target) is null) return $"There's no branch called '{target}'.";
            if (MoveExisting)
            {
                if (BranchToMove is not { } b) return "Choose the branch to move.";
                if (b.Name == target) return "Choose a different branch to rebase onto.";
                if (b.OtherWorktreePath is { } path)
                    return $"{b.Name} is checked out in the worktree at {path}. Open that worktree to rebase it there, or create a new branch instead.";
                return null;
            }
            var name = NewBranchName.Trim();
            if (name.Length == 0) return "Enter a name for the new branch.";
            if (name.Contains(' ') || name.Contains("..") || name.EndsWith('/') || name.StartsWith('-') || name.EndsWith(".lock"))
                return "That isn't a valid branch name.";
            if (_localBranches.Contains(name)) return $"A branch named '{name}' already exists.";
            return null;
        }
    }

    public bool IsValid => Error is null;

    partial void OnTargetChanged(string value) => _ = UpdatePreviewAsync();
    partial void OnMoveExistingChanged(bool value) => _ = UpdatePreviewAsync();
    partial void OnBranchToMoveChanged(BranchChoice? value) => _ = UpdatePreviewAsync();
    partial void OnNewBranchNameChanged(string value) => _ = UpdatePreviewAsync();

    private async Task UpdatePreviewAsync()
    {
        var request = ++_previewRequest;
        var target = Target.Trim();
        var names = Commits.Count switch
        {
            1 => Commits[0].ShortSha,
            2 => $"{Commits[1].ShortSha} and {Commits[0].ShortSha}",
            _ => $"{Commits.Count} commits ({Commits[^1].ShortSha} to {Commits[0].ShortSha})",
        };
        var result = ResultBranch.Length == 0 ? "the new branch" : ResultBranch;
        var preview = $"{names} will be replayed, oldest first, on top of {(target.Length == 0 ? "the chosen branch" : target)}. " +
                      (MoveExisting ? $"{result} will then end at the new copy of {Commits[0].ShortSha}."
                          : $"The new branch {result} will end there; the original commits stay where they are.");
        var warnings = new List<string>();

        if (MoveExisting && BranchToMove is { } moved && target.Length > 0 && _describeTarget(target) is not null && moved.Name != target)
        {
            var dropped = await _droppedCommits(target, moved.Name);
            if (request != _previewRequest) return;
            if (dropped.Count > 0)
            {
                var list = dropped.Count <= 5 ? string.Join(", ", dropped) : $"{string.Join(", ", dropped.Take(5))} and {dropped.Count - 5} more";
                warnings.Add($"{moved.Name} will no longer include the commits below the selection: {list}.");
            }
            if (moved.HasUpstream) warnings.Add($"{moved.Name} is on the remote: you'll need to force push it afterwards.");
        }
        if (ResultBranch.Length > 0 && ResultBranch != _currentBranch)
            warnings.Add($"This worktree will be switched to {result}.");

        Preview = preview;
        Warnings = warnings;
    }

    /// <summary>Call after construction so the first preview is computed.</summary>
    public Task InitializeAsync() => UpdatePreviewAsync();

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var i = 2; _localBranches.Contains(candidate); i++) candidate = $"{name}-{i}";
        return candidate;
    }
}
