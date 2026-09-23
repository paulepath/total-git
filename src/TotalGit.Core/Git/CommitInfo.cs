namespace TotalGit.Core.Git;

public sealed record CommitInfo(
    string Sha,
    IReadOnlyList<string> ParentShas,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string MessageShort,
    bool IsWorkingTree = false)
{
    /// <summary>Pseudo-SHA of the synthetic "work in progress" row.</summary>
    public const string WorkingTreeSha = "WORKING-TREE";

    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;
    public bool IsMerge => ParentShas.Count > 1;
}
