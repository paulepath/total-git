namespace TotalGit.Core.Git;

public sealed record CommitInfo(
    string Sha,
    IReadOnlyList<string> ParentShas,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string MessageShort)
{
    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;
    public bool IsMerge => ParentShas.Count > 1;
}
