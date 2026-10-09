using System.Globalization;
using Avalonia.Media;
using TotalGit.App.Services;
using TotalGit.Core.Hosting;

namespace TotalGit.App.ViewModels;

/// <summary>A reviewer's line on the hover card.</summary>
public sealed record PrReviewerLine(PrPerson Person, string StateText, IBrush StateBrush);

/// <summary>
/// The hover card of a pull request row: everything needed to tell whether it can merge and who it's waiting on,
/// without opening it.
/// </summary>
public sealed class PrCardViewModel
{
    /// <summary>Width of the checks bar, in pixels.</summary>
    public const double BarWidth = 316;

    public PrCardViewModel(PullRequestSummary pr, PrPerson author, IReadOnlyList<PrPerson> reviewers, string? ticket)
    {
        Title = pr.Title;
        NumberText = $"#{pr.Number}";
        IsDraft = pr.IsDraft;
        Author = author;
        Ticket = ticket;
        var opened = pr.CreatedAt is { } created ? $"{pr.Author.DisplayName} opened {DateText.Relative(created)}" : $"Opened by {pr.Author.DisplayName}";
        AuthorLine = $"{opened} · updated {DateText.Relative(pr.UpdatedAt)}";
        HeadRef = pr.IsCrossRepository ? $"{pr.HeadRef} (fork)" : pr.HeadRef;
        BaseRef = pr.BaseRef;
        (MergeNote, MergeNoteBrush) = pr.MergeState switch
        {
            MergeState.Conflicting => ("has conflicts", PrColors.Failure),
            MergeState.Behind => ($"behind {pr.BaseRef}", PrColors.Pending),
            _ => ((string?)null, PrColors.Neutral),
        };

        var rail = PullRequestTriage.Rail(pr);
        RailBrush = PrRailColors.For(rail);
        StatusText = rail switch
        {
            PrRail.Draft => "Draft",
            PrRail.YourReview => "Waiting for your review",
            PrRail.Blocked when pr.MergeState == MergeState.Conflicting => "Blocked: conflicts with " + pr.BaseRef,
            PrRail.Blocked when pr.Checks == ChecksState.Failure => "Blocked: checks failed",
            PrRail.Blocked => "Blocked: changes requested",
            PrRail.Waiting when pr.Checks == ChecksState.Pending => "Checks running",
            PrRail.Waiting => "Waiting for review",
            _ => "Ready to merge",
        };

        var runs = pr.CheckRuns;
        var passed = runs.Count(c => c.Status is CheckStatus.Success or CheckStatus.Neutral or CheckStatus.Skipped);
        var running = runs.Count(c => c.Status == CheckStatus.Pending);
        var failed = runs.Count(c => c.Status is CheckStatus.Failure or CheckStatus.Cancelled);
        HasChecks = runs.Count > 0;
        CheckSummary = string.Join(" · ", new[] { (passed, "passed"), (running, "running"), (failed, "failed") }
            .Where(x => x.Item1 > 0).Select(x => $"{x.Item1} {x.Item2}"));
        var total = Math.Max(1, passed + running + failed);
        PassedWidth = BarWidth * passed / total;
        RunningWidth = BarWidth * running / total;
        FailedWidth = BarWidth * failed / total;
        const int shownFailures = 4;
        var failing = runs.Where(c => c.Status is CheckStatus.Failure or CheckStatus.Cancelled).ToList();
        FailingChecks = failing.Take(shownFailures).Select(c => c.Name).ToList();
        MoreFailing = failing.Count > shownFailures ? $"and {failing.Count - shownFailures} more" : null;

        Reviewers = reviewers.Select(p => new PrReviewerLine(p, StateText(p), p.StateBrush ?? PrColors.Neutral)).ToList();

        if (pr.Additions is { } added && pr.Deletions is { } deleted)
        {
            AddedText = "+" + added.ToString("N0", CultureInfo.CurrentCulture);
            DeletedText = "−" + deleted.ToString("N0", CultureInfo.CurrentCulture);
            FilesText = pr.ChangedFiles is { } files ? $"in {files:N0} file{(files == 1 ? "" : "s")}" : "";
        }
    }

    public string Title { get; }
    public string NumberText { get; }
    public bool IsDraft { get; }
    public PrPerson Author { get; }
    public string AuthorLine { get; }
    public string? Ticket { get; }
    public bool HasTicket => Ticket is not null;

    public string HeadRef { get; }
    public string BaseRef { get; }
    public string? MergeNote { get; }
    public IBrush MergeNoteBrush { get; }
    public bool HasMergeNote => MergeNote is not null;

    public IBrush RailBrush { get; }
    public string StatusText { get; }

    public bool HasChecks { get; }
    public string CheckSummary { get; }
    public double PassedWidth { get; }
    public double RunningWidth { get; }
    public double FailedWidth { get; }
    public IReadOnlyList<string> FailingChecks { get; }
    public string? MoreFailing { get; }
    public bool HasMoreFailing => MoreFailing is not null;

    public IReadOnlyList<PrReviewerLine> Reviewers { get; }
    public bool HasReviewers => Reviewers.Count > 0;

    public string? AddedText { get; }
    public string? DeletedText { get; }
    public string? FilesText { get; }
    public bool HasSize => AddedText is not null;

    private static string StateText(PrPerson p) => p.State switch
    {
        ReviewState.Approved => "approved",
        ReviewState.ChangesRequested => "changes requested",
        ReviewState.Commented => "commented",
        ReviewState.Dismissed => "dismissed",
        _ => "review requested",
    };
}

/// <summary>The status rail's colours.</summary>
public static class PrRailColors
{
    public static readonly IBrush YourReview = new SolidColorBrush(Color.Parse("#5AA9F2"));
    public static readonly IBrush Draft = new SolidColorBrush(Color.Parse("#5C636D"));

    public static IBrush For(PrRail rail) => rail switch
    {
        PrRail.Ready => PrColors.Success,
        PrRail.Blocked => PrColors.Failure,
        PrRail.Waiting => PrColors.Pending,
        PrRail.YourReview => YourReview,
        _ => Draft,
    };
}
