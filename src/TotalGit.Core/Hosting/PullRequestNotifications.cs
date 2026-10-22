namespace TotalGit.Core.Hosting;

public enum InboxEventKind
{
    ReviewRequested,
    Assigned,
    ChangesRequested,
    Approved,
    ChecksFailed,
    Commented,
}

/// <summary>Something about a pull request worth telling the user, with an id that stays the same between polls.</summary>
/// <param name="By">Who did it (a reviewer or commenter), when it was someone.</param>
public sealed record InboxEvent(string Id, InboxEventKind Kind, InboxPullRequest PullRequest, PrUser? By = null);

/// <summary>One notification to show: about one pull request, or (<see cref="Url"/> null) a summary of several.</summary>
/// <param name="Attribution">Where it's from: the repository, or "GitHub" for a summary.</param>
public sealed record PrNotification(string Title, string Text, string Attribution, string? Url, IReadOnlyList<InboxEvent> Events);

/// <summary>
/// Turns polls of the inbox into notifications: what's new since the last poll, one notification per pull request.
/// </summary>
public static class PullRequestNotifications
{
    /// <summary>More pull requests than this at once make one summary notification instead.</summary>
    public const int MaxNotifications = 4;

    /// <summary>
    /// Everything notifiable in the inbox now. Review requests and assignments by their event on GitHub (asked again
    /// after reviewing is new again); reviews and comments by id; failed checks once per commit. The user's own
    /// reviews and comments, and anything by bots, are left out.
    /// </summary>
    public static IReadOnlyList<InboxEvent> Events(Inbox inbox)
    {
        var events = new List<InboxEvent>();
        foreach (var pr in inbox.PullRequests)
        {
            var key = $"{pr.Repository}#{pr.Number}";
            if (pr.ReviewRequested) events.Add(new($"rr:{key}:{pr.ReviewRequestId}", InboxEventKind.ReviewRequested, pr, pr.Author));
            if (pr.Assigned) events.Add(new($"as:{key}:{pr.AssignmentId}", InboxEventKind.Assigned, pr));
            if (!pr.IsViewerAuthor) continue;

            bool Others(PrUser who) => !who.IsBot && !string.Equals(who.Login, inbox.Viewer, StringComparison.OrdinalIgnoreCase);
            foreach (var review in pr.Reviews.Where(r => Others(r.Author)))
            {
                switch (review.State)
                {
                    case ReviewState.Approved: events.Add(new($"ap:{review.Id}", InboxEventKind.Approved, pr, review.Author)); break;
                    case ReviewState.ChangesRequested: events.Add(new($"cr:{review.Id}", InboxEventKind.ChangesRequested, pr, review.Author)); break;
                    case ReviewState.Commented when review.HasBody: events.Add(new($"cm:{review.Id}", InboxEventKind.Commented, pr, review.Author)); break;
                }
            }
            foreach (var comment in pr.Comments.Where(c => Others(c.Author)))
                events.Add(new($"cm:{comment.Id}", InboxEventKind.Commented, pr, comment.Author));
            if (pr.Checks == ChecksState.Failure) events.Add(new($"cf:{key}:{pr.HeadSha}", InboxEventKind.ChecksFailed, pr));
        }
        return events;
    }

    /// <summary>How long an event is remembered after it was last there.</summary>
    public static readonly TimeSpan Remember = TimeSpan.FromDays(30);

    /// <summary>
    /// The inbox's events not seen before, and what to remember as seen now (the events and pull requests, with when
    /// each was last there). GitHub's search sometimes leaves pull requests out for a poll or two, so:
    /// <list type="bullet">
    /// <item>what has gone is still remembered for <see cref="Remember"/>, and its return isn't news;</item>
    /// <item>a pull request turning up for the first time brings only its review request or assignment as news, not
    /// the reviews, comments and checks it already had.</item>
    /// </list>
    /// With nothing remembered yet (the first poll ever) nothing is new: no flood of old news.
    /// </summary>
    public static IReadOnlyList<InboxEvent> Diff(IReadOnlyDictionary<string, DateTimeOffset>? seen, Inbox inbox,
        DateTimeOffset now, out Dictionary<string, DateTimeOffset> nowSeen)
    {
        var events = Events(inbox);
        nowSeen = (seen ?? new Dictionary<string, DateTimeOffset>()).Where(kv => now - kv.Value < Remember)
            .ToDictionary(StringComparer.Ordinal);
        var news = seen is null ? [] : events
            .Where(e => !seen.ContainsKey(e.Id))
            .Where(e => e.Kind is InboxEventKind.ReviewRequested or InboxEventKind.Assigned || seen.ContainsKey(Key(e.PullRequest)))
            .DistinctBy(e => e.Id).ToList();
        foreach (var e in events) nowSeen[e.Id] = now;
        foreach (var pr in inbox.PullRequests) nowSeen[Key(pr)] = now;
        return news;
    }

    private static string Key(InboxPullRequest pr) => $"pr:{pr.Repository}#{pr.Number}";

    /// <summary>One notification per pull request, most important news first; past <see cref="MaxNotifications"/>, one summary.</summary>
    public static IReadOnlyList<PrNotification> Group(IReadOnlyList<InboxEvent> events)
    {
        var byPr = events.GroupBy(e => e.PullRequest.Url).Select(g => g.ToList()).ToList();
        if (byPr.Count == 0) return [];
        if (byPr.Count > MaxNotifications)
        {
            var lines = byPr.Take(3).Select(g => $"#{g[0].PullRequest.Number} {g[0].PullRequest.Title}: {Headline(g, capitalise: false)}");
            var more = byPr.Count > 3 ? $"\n…and {byPr.Count - 3} more" : "";
            return [new($"{byPr.Count} pull requests have news", string.Join('\n', lines) + more, "GitHub", null, events)];
        }
        return byPr.Select(g => new PrNotification(Headline(g, capitalise: true), $"#{g[0].PullRequest.Number} {g[0].PullRequest.Title}",
            g[0].PullRequest.Repository, g[0].PullRequest.Url, g)).ToList();
    }

    /// <summary>What happened, e.g. "Alice approved, bob left 2 comments".</summary>
    private static string Headline(IReadOnlyList<InboxEvent> events, bool capitalise)
    {
        var parts = events.GroupBy(e => e.Kind).OrderBy(g => g.Key).Select(g =>
        {
            var people = Names(g.Select(e => e.By));
            return g.Key switch
            {
                InboxEventKind.ReviewRequested => $"{people} asked for your review",
                InboxEventKind.Assigned => "you were assigned",
                InboxEventKind.ChangesRequested => $"{people} requested changes",
                InboxEventKind.Approved => $"{people} approved",
                InboxEventKind.ChecksFailed => "checks failed",
                _ => g.Count() == 1 ? $"{people} commented" : $"{people} left {g.Count()} comments",
            };
        }).ToList();
        var text = string.Join(", ", parts);
        return capitalise && text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;
    }

    /// <summary>The repository (<c>owner/name</c>) and number in a pull request's web address, or null for anything else.</summary>
    public static (string Repository, int Number)? ParseUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length >= 4 && parts[2] == "pull" && int.TryParse(parts[3], out var number) ? ($"{parts[0]}/{parts[1]}", number) : null;
    }

    /// <summary>"alice", "alice and bob", "alice, bob and carol", "alice, bob and 3 others".</summary>
    private static string Names(IEnumerable<PrUser?> users)
    {
        var names = users.OfType<PrUser>().Select(u => u.Login).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count switch
        {
            0 => "Someone",
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            3 => $"{names[0]}, {names[1]} and {names[2]}",
            _ => $"{names[0]}, {names[1]} and {names.Count - 2} others",
        };
    }
}
