namespace TotalGit.Core.Git;

public enum RefKind
{
    LocalBranch,
    RemoteBranch,
    Tag,
    DetachedHead,
}

public sealed record RefInfo(string Name, RefKind Kind, string TargetSha, bool IsCurrent);
