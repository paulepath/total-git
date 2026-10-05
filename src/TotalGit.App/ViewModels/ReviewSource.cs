using System.Security.Cryptography;
using System.Text;
using TotalGit.Core.Hosting;

namespace TotalGit.App.ViewModels;

/// <summary>
/// What a review window reviews: a pull request on the host, or changes that haven't gone to one yet (a branch
/// against the branch it came off, or a run of commits). Local reviews have no comments, checks or host sync.
/// </summary>
public abstract record ReviewSource
{
    /// <summary>Which review the reviewed marks belong to; also tells review windows apart.</summary>
    public abstract string MarksKey { get; }
}

public sealed record PullRequestSource(IPullRequestProvider Provider, PullRequestSummary Summary) : ReviewSource
{
    public override string MarksKey => ReviewMarks.Key(Provider.Host, Summary.Number);
}

/// <summary>A branch against its base, like a pull request: the changes since they split. Re-read on every reload.</summary>
public sealed record BranchReviewSource(string Repository, string HeadRef, string BaseRef) : ReviewSource
{
    // By name, so the marks last through new commits and rebases.
    public override string MarksKey => $"local:{Repository}|{HeadRef}..{BaseRef}".ToLowerInvariant();
}

/// <summary>A run of commits on one line, from the oldest's parent to the newest.</summary>
public sealed record CommitsReviewSource(string Repository, string OldestSha, string NewestSha, int Count) : ReviewSource
{
    public override string MarksKey => $"local:{Repository}|{OldestSha}..{NewestSha}".ToLowerInvariant();
}

internal static class ReviewSourceExtensions
{
    /// <summary>A short, ref-safe name for a review ("pr/12", or a hash of a local review's key).</summary>
    public static string RefName(this ReviewSource source) => source is PullRequestSource pr
        ? pr.Summary.Number.ToString()
        : "local-" + Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(source.MarksKey)))[..12];
}
