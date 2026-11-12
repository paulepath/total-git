namespace TotalGit.Core.Git;

public sealed record CommitInfo(
    string Sha,
    IReadOnlyList<string> ParentShas,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string MessageShort,
    bool IsWorkingTree = false,
    string? WorktreePath = null)
{
    /// <summary>Pseudo-SHA of the synthetic "work in progress" row of the open worktree.</summary>
    public const string WorkingTreeSha = "WORKING-TREE";

    /// <summary>Pseudo-SHA of another worktree's "work in progress" row.</summary>
    public static string OtherWorkingTreeSha(string worktreePath) => $"{WorkingTreeSha}:{worktreePath}";

    /// <summary>The WIP row of a worktree other than the one the tab shows.</summary>
    public bool IsOtherWorktree => IsWorkingTree && WorktreePath is not null;

    private const string NewBranchPrefix = "NEW-BRANCH:";

    /// <summary>Pseudo-SHA of the row standing for a branch with no commits yet, just above the commit it was made from.</summary>
    public static string NewBranchSha(string branch) => NewBranchPrefix + branch;

    /// <summary>The branch a new branch's row stands for; null for any other SHA.</summary>
    public static string? NewBranchOf(string sha) => sha.StartsWith(NewBranchPrefix, StringComparison.Ordinal) ? sha[NewBranchPrefix.Length..] : null;

    private const string PersonFoldPrefix = "PERSON-FOLD:";

    /// <summary>Pseudo-SHA of the row standing for one person's folded branches.</summary>
    public static string PersonFoldSha(string email) => PersonFoldPrefix + email.ToLowerInvariant();

    /// <summary>The person (email, lower case) a folded-person row stands for; null for any other SHA.</summary>
    public static string? PersonFoldOf(string sha) => sha.StartsWith(PersonFoldPrefix, StringComparison.Ordinal) ? sha[PersonFoldPrefix.Length..] : null;

    /// <summary>A row that isn't a commit, by its SHA: a WIP row, a new branch's row or a folded person's row.</summary>
    public static bool IsPlaceholderSha(string sha) =>
        sha.StartsWith(WorkingTreeSha, StringComparison.Ordinal) || sha.StartsWith(NewBranchPrefix, StringComparison.Ordinal)
        || sha.StartsWith(PersonFoldPrefix, StringComparison.Ordinal);

    /// <summary>For a new branch's row, the branch.</summary>
    public string? NewBranchName { get; init; }

    /// <summary>The row standing for one person's folded branches (the author fields name them).</summary>
    public bool IsPersonFold { get; init; }

    /// <summary>A row that isn't a commit: a WIP row, a new branch's row or a folded person's row.</summary>
    public bool IsPlaceholder => IsWorkingTree || NewBranchName is not null || IsPersonFold;

    /// <summary>When the commit was written (a rebase rewrites this but keeps the author date); null when unknown.</summary>
    public DateTimeOffset? CommitDate { get; init; }

    /// <summary>The full message retained with loaded history for searching commit bodies.</summary>
    public string? FullMessage { get; init; }

    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;
    public bool IsMerge => ParentShas.Count > 1;
}
