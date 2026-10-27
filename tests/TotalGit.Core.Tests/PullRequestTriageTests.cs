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
    public void Ready_to_merge_needs_an_approval_passing_checks_and_nothing_in_the_way()
    {
        Assert.True(PullRequestTriage.IsReadyToMerge(Pr()));
        Assert.True(PullRequestTriage.IsReadyToMerge(Pr(checks: ChecksState.None, merge: MergeState.Unknown)));
        Assert.False(PullRequestTriage.IsReadyToMerge(Pr(draft: true)));
        Assert.False(PullRequestTriage.IsReadyToMerge(Pr(review: ReviewDecision.ReviewRequired)));
        Assert.False(PullRequestTriage.IsReadyToMerge(Pr(checks: ChecksState.Pending)));
        Assert.False(PullRequestTriage.IsReadyToMerge(Pr(checks: ChecksState.Failure)));
        Assert.False(PullRequestTriage.IsReadyToMerge(Pr(merge: MergeState.Behind)));
        Assert.False(PullRequestTriage.IsReadyToMerge(Pr(merge: MergeState.Blocked))); // e.g. a second approval is required
        Assert.False(PullRequestTriage.IsReadyToMerge(Pr(merge: MergeState.Conflicting)));

        // No required reviews: an approval counts, unless someone asked for changes; no reviews at all doesn't.
        var bob = new Reviewer("bob", null, ReviewState.Approved, IsRequested: false);
        var carol = new Reviewer("carol", null, ReviewState.ChangesRequested, IsRequested: false);
        Assert.False(PullRequestTriage.IsReadyToMerge(Pr(review: ReviewDecision.None)));
        Assert.True(PullRequestTriage.IsReadyToMerge(Pr(review: ReviewDecision.None) with { Reviewers = [bob] }));
        Assert.False(PullRequestTriage.IsReadyToMerge(Pr(review: ReviewDecision.None) with { Reviewers = [bob, carol] }));
    }

    [Fact]
    public void Merge_problems_say_what_is_in_the_way()
    {
        Assert.Empty(PullRequestTriage.MergeProblems(Pr()));
        Assert.Equal(["it still needs an approving review", "checks failed", "it's behind main"],
            PullRequestTriage.MergeProblems(Pr(review: ReviewDecision.ReviewRequired, checks: ChecksState.Failure, merge: MergeState.Behind)));
        Assert.Equal(["the branch's rules hold it (more approvals needed?)"], PullRequestTriage.MergeProblems(Pr(merge: MergeState.Blocked)));
        Assert.Equal(["changes were requested"], PullRequestTriage.MergeProblems(Pr(review: ReviewDecision.ChangesRequested, merge: MergeState.Blocked)));
    }

    [Fact]
    public void Groups_put_drafts_last_and_your_reviews_first()
    {
        Assert.Equal(PrGroup.Drafts, PullRequestTriage.Group(Pr(draft: true, yourReview: true)));
        Assert.Equal(PrGroup.NeedsYourReview, PullRequestTriage.Group(Pr(yourReview: true)));
        Assert.Equal(PrGroup.Yours, PullRequestTriage.Group(Pr(yours: true)));
        Assert.Equal(PrGroup.Others, PullRequestTriage.Group(Pr()));
    }

    [Fact]
    public void Reviewed_without_approving_still_needs_your_review()
    {
        // GitHub drops the request once you review: commenting or asking for changes leaves it waiting for you.
        var reviewed = Pr() with { ViewerReviewedNotApproved = true };
        Assert.Equal(PrGroup.NeedsYourReview, PullRequestTriage.Group(reviewed));
        Assert.Equal(PrRail.YourReview, PullRequestTriage.Rail(reviewed));
        Assert.Equal(PrGroup.Others, PullRequestTriage.Group(Pr()));
        Assert.Equal(PrGroup.Yours, PullRequestTriage.Group(Pr(yours: true) with { ViewerReviewedNotApproved = true })); // your own
        Assert.False((reviewed with { State = PullRequestState.Merged }).WaitsForViewer);
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
