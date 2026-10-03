using System.Text.RegularExpressions;

namespace TotalGit.Core.Hosting;

/// <summary>Where a pull request stands, as one colour on the left of its row.</summary>
public enum PrRail
{
    /// <summary>Not ready for review yet.</summary>
    Draft,

    /// <summary>The signed-in user is asked to review it.</summary>
    YourReview,

    /// <summary>Checks failed, changes were requested, or it conflicts with its target.</summary>
    Blocked,

    /// <summary>Checks are running or reviews are still needed.</summary>
    Waiting,

    /// <summary>Checks passed and nothing is holding it up.</summary>
    Ready,
}

/// <summary>The headings pull requests are listed under, in display order.</summary>
public enum PrGroup
{
    NeedsYourReview,
    Yours,
    Others,
    Drafts,
}

/// <summary>Sorts pull requests into the sidebar's groups and colours, and finds the issue-tracker ticket they're for.</summary>
public static partial class PullRequestTriage
{
    public static PrRail Rail(PullRequestSummary pr)
    {
        if (pr.IsDraft) return PrRail.Draft;
        if (pr.ViewerReviewRequested) return PrRail.YourReview;
        if (pr.Checks == ChecksState.Failure || pr.ReviewDecision == ReviewDecision.ChangesRequested || pr.MergeState == MergeState.Conflicting)
            return PrRail.Blocked;
        if (pr.Checks == ChecksState.Pending || pr.ReviewDecision == ReviewDecision.ReviewRequired) return PrRail.Waiting;
        return PrRail.Ready;
    }

    public static PrGroup Group(PullRequestSummary pr) =>
        pr.IsDraft ? PrGroup.Drafts
        : pr.ViewerReviewRequested ? PrGroup.NeedsYourReview
        : pr.IsViewerAuthor ? PrGroup.Yours
        : PrGroup.Others;

    /// <summary>
    /// The ticket key at the start of a title ("E4-2007: Fix login", "[E4-2007] Fix login") and the title without it,
    /// or a key anywhere else in the title (left in place). Null when there's none.
    /// </summary>
    public static (string? Key, string Title) Ticket(string title)
    {
        var lead = LeadingKey().Match(title);
        if (lead.Success)
        {
            var rest = title[lead.Length..].Trim();
            return (lead.Groups["key"].Value, rest.Length > 0 ? rest : title);
        }
        var any = AnyKey().Match(title);
        return any.Success ? (any.Groups["key"].Value, title) : (null, title);
    }

    /// <summary>
    /// A ticket key in a branch name ("feature/e4-2361-docker" → "E4-2361"), counted only when its project is one
    /// <paramref name="knownProjects"/> uses, so "release-1" or "sync-2" aren't mistaken for tickets.
    /// </summary>
    public static string? TicketFromBranch(string branch, IReadOnlySet<string> knownProjects)
    {
        foreach (Match m in BranchKey().Matches(branch))
        {
            var key = m.Groups["key"].Value.ToUpperInvariant();
            if (knownProjects.Contains(key[..key.IndexOf('-')])) return key;
        }
        return null;
    }

    /// <summary>
    /// Where a ticket key is in a branch name, and the key in capitals. Capitals ("feature/E4-2361-docker") always
    /// count; lower case ("e4-2361") only for a project in <paramref name="knownProjects"/>, so "fix-2" isn't a ticket.
    /// </summary>
    public static (int Start, int Length, string Key)? KeyInBranch(string name, IReadOnlySet<string>? knownProjects)
    {
        foreach (Match m in BranchKey().Matches(name))
        {
            var g = m.Groups["key"];
            var key = g.Value.ToUpperInvariant();
            if (g.Value == key || knownProjects?.Contains(key[..key.IndexOf('-')]) == true) return (g.Index, g.Length, key);
        }
        return null;
    }

    /// <summary>The project part of a key ("E4" for "E4-2007").</summary>
    public static string Project(string key) => key[..key.IndexOf('-')];

    [GeneratedRegex(@"^\s*\[?(?<key>[A-Z][A-Z0-9]{0,9}-\d+)\]?\s*(?:[:\-–|]\s*)?")]
    private static partial Regex LeadingKey();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<key>[A-Z][A-Z0-9]{0,9}-\d+)(?![A-Za-z0-9])")]
    private static partial Regex AnyKey();

    [GeneratedRegex(@"(?:^|/)(?<key>[A-Za-z][A-Za-z0-9]{0,9}-\d+)(?=$|[-_/])")]
    private static partial Regex BranchKey();
}
