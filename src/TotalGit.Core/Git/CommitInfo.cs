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

    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;
    public bool IsMerge => ParentShas.Count > 1;
}
