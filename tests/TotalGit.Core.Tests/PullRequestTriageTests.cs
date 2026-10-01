using TotalGit.Core.Hosting;

namespace TotalGit.Core.Tests;

public sealed class PullRequestTriageTests
{
    private static PullRequestSummary Pr(bool draft = false, bool yourReview = false, bool yours = false,
        ChecksState checks = ChecksState.Success, ReviewDecision review = ReviewDecision.Approved, MergeState merge = MergeState.Clean) =>
        new(1, "t", new PrUser("a", null), draft, PullRequestState.Open, "main", "b", "sha", false, DateTimeOffset.UnixEpoch,
            checks, review, yourReview, "u")
        { IsViewerAuthor = yours, MergeState = merge };

    [Fact]
    public void Rail_shows_the_most_pressing_state()
    {
        Assert.Equal(PrRail.Draft, PullRequestTriage.Rail(Pr(draft: true, yourReview: true)));
        Assert.Equal(PrRail.YourReview, PullRequestTriage.Rail(Pr(yourReview: true, checks: ChecksState.Failure)));
        Assert.Equal(PrRail.Blocked, PullRequestTriage.Rail(Pr(checks: ChecksState.Failure)));
        Assert.Equal(PrRail.Blocked, PullRequestTriage.Rail(Pr(review: ReviewDecision.ChangesRequested)));
        Assert.Equal(PrRail.Blocked, PullRequestTriage.Rail(Pr(merge: MergeState.Conflicting)));
        Assert.Equal(PrRail.Waiting, PullRequestTriage.Rail(Pr(checks: ChecksState.Pending)));
        Assert.Equal(PrRail.Waiting, PullRequestTriage.Rail(Pr(review: ReviewDecision.ReviewRequired)));
        Assert.Equal(PrRail.Ready, PullRequestTriage.Rail(Pr()));
        Assert.Equal(PrRail.Ready, PullRequestTriage.Rail(Pr(checks: ChecksState.None, review: ReviewDecision.None)));
    }

    [Fact]
    public void Groups_put_drafts_last_and_your_reviews_first()
    {
        Assert.Equal(PrGroup.Drafts, PullRequestTriage.Group(Pr(draft: true, yourReview: true)));
        Assert.Equal(PrGroup.NeedsYourReview, PullRequestTriage.Group(Pr(yourReview: true, yours: true)));
        Assert.Equal(PrGroup.Yours, PullRequestTriage.Group(Pr(yours: true)));
        Assert.Equal(PrGroup.Others, PullRequestTriage.Group(Pr()));
    }

    [Theory]
    [InlineData("E4-2007: [Load] [Manager] My Organisation Trackers", "E4-2007", "[Load] [Manager] My Organisation Trackers")]
    [InlineData("[E4-2391] complete bulk upload", "E4-2391", "complete bulk upload")]
    [InlineData("ABC-12 - Fix it", "ABC-12", "Fix it")]
    [InlineData("Restore scores (E4-2277)", "E4-2277", "Restore scores (E4-2277)")]
    [InlineData("fix(local): run WSL keepalive", null, "fix(local): run WSL keepalive")]
    [InlineData("Speed up FE tests: per-function date-fns", null, "Speed up FE tests: per-function date-fns")]
    [InlineData("E4-2007", "E4-2007", "E4-2007")]
    public void Finds_the_ticket_in_a_title(string title, string? key, string rest)
    {
        Assert.Equal((key, rest), PullRequestTriage.Ticket(title));
    }

    [Fact]
    public void Branch_tickets_count_only_for_known_projects()
    {
        var known = new HashSet<string> { "E4" };

        Assert.Equal("E4-2361", PullRequestTriage.TicketFromBranch("feature/e4-2361-docker", known));
        Assert.Equal("E4-12", PullRequestTriage.TicketFromBranch("E4-12", known));
        Assert.Null(PullRequestTriage.TicketFromBranch("release-1", known));
        Assert.Null(PullRequestTriage.TicketFromBranch("hot-fix/sync-2", known));
        Assert.Null(PullRequestTriage.TicketFromBranch("feature/e4x-2361", known));
    }
}
