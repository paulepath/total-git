using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Git;
using TotalGit.Core.Hosting;

namespace TotalGit.App.ViewModels;

// What the threads' comments can do: reply, resolve, react, edit, delete, hide, and commit suggested changes
// (which GitHub can't do through its API, so the commit is made here and pushed to the pull request's branch).
public sealed partial class PullRequestReviewViewModel
{
    public PrCapabilities Capabilities => Provider?.Capabilities ?? PrCapabilities.None;
    public PrUser? Viewer => Pr.Details?.Viewer;
    public string ViewerLogin => Viewer?.Login ?? "";
    public string PullRequestAuthor => Summary.Author.Login;
    public AvatarCache? Avatars => _ctx.Avatars;

    public Task<bool> ReplyAsync(ReviewThread thread, string body) =>
        Provider is { } p ? PostAsync("Replying…", () => p.ReplyAsync(Number, thread, body)) : Task.FromResult(false);

    public Task<bool> SetResolvedAsync(ReviewThread thread, bool resolved) =>
        Provider is { } p ? PostAsync(resolved ? "Resolving…" : "Unresolving…", () => p.SetResolvedAsync(thread, resolved)) : Task.FromResult(false);

    public async Task<bool> ReactAsync(PrComment comment, Reaction reaction, bool add)
    {
        if (Provider is not { } provider) return false;
        try
        {
            await Task.Run(() => provider.ReactAsync(comment, reaction, add));
            return true;
        }
        catch (HostException ex)
        {
            ShowError(ex.Message);
            return false;
        }
    }

    public Task<bool> EditAsync(PrComment comment, string body) =>
        Provider is { } p ? PostAsync("Saving…", () => p.EditCommentAsync(comment, body)) : Task.FromResult(false);

    public async Task<bool> DeleteAsync(PrComment comment)
    {
        if (Provider is not { } provider || Dialogs is null) return false;
        var preview = CommentText.Title(comment.Body, 80);
        if (!await Dialogs.ShowFormAsync(new FormSpec("Delete comment",
                $"Delete your comment{(preview.Length > 0 ? $" \"{preview}\"" : "")}? It's removed from the pull request for everyone, and can't be brought back.",
                "Delete", [])))
            return false;
        return await PostAsync("Deleting…", () => provider.DeleteCommentAsync(comment));
    }

    public Task<bool> SetHiddenAsync(PrComment comment, HideReason? reason) =>
        Provider is { } p ? PostAsync(reason is null ? "Showing…" : "Hiding…", () => p.SetHiddenAsync(comment, reason)) : Task.FromResult(false);

    public Task<IReadOnlyList<CommentEdit>> GetEditsAsync(PrComment comment) =>
        Provider is { } p ? Task.Run(() => p.GetEditsAsync(comment)) : Task.FromResult<IReadOnlyList<CommentEdit>>([]);

    public Task<IReadOnlyList<PrUser>> SearchMentionsAsync(string query) =>
        Provider is { } p ? Task.Run(() => p.SearchMentionsAsync(query)) : Task.FromResult<IReadOnlyList<PrUser>>([]);

    public Task CopyAsync(string text) => Dialogs?.CopyToClipboardAsync(text) ?? Task.CompletedTask;

    // ------------------------------------------------------------------ merging

    private PullRequestSummary Latest => Pr.Details?.Summary ?? Summary;

    /// <summary>An open pull request that isn't a draft, on a host that merges: the header's Merge… button.</summary>
    public bool CanMerge => IsPullRequest && Capabilities.HasFlag(PrCapabilities.Merge) && Latest is { State: PullRequestState.Open, IsDraft: false };

    /// <summary>Approved, checks passed and nothing in the way: the Merge… button is green.</summary>
    public bool IsReadyToMerge => CanMerge && PullRequestTriage.IsReadyToMerge(Latest);

    public string MergeToolTip => IsReadyToMerge
        ? "Ready to merge: approved, checks passed, and GitHub has nothing against it"
        : PullRequestTriage.MergeProblems(Latest) is { Count: > 0 } problems ? "GitHub may refuse: " + string.Join(", ", problems) : "Merge on GitHub";

    [RelayCommand]
    private async Task MergeAsync()
    {
        if (Provider is not { } provider || Dialogs is null || !CanMerge) return;
        MergeOptions options;
        try
        {
            options = await Task.Run(() => provider.GetMergeOptionsAsync());
        }
        catch (HostException ex)
        {
            ShowError(ex.Message);
            return;
        }
        var pr = Latest;
        if (await PullRequestMerging.AskAsync(pr, options, Dialogs.ShowFormAsync) is not { } choice) return;

        string message;
        IsBusy = true;
        BusyText = $"Merging #{pr.Number}…";
        try
        {
            message = await PullRequestMerging.MergeAsync(provider, pr, choice, _ctx.WorkingDirectory);
        }
        catch (HostException ex)
        {
            ShowError($"Couldn't merge: {ex.Message}");
            return;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
        ShowInfo(message);
        if (_ctx.AfterMerge is { } after) await after(message);
        try
        {
            Pr.Details = await Task.Run(() => provider.GetPullRequestAsync(Number));
        }
        catch (HostException)
        {
            // The merge is done; the window just shows the older state.
        }
    }

    /// <summary>The details changed: whether it can (still) be merged may have.</summary>
    private void OnMergeStateChanged()
    {
        OnPropertyChanged(nameof(CanMerge));
        OnPropertyChanged(nameof(IsReadyToMerge));
        OnPropertyChanged(nameof(MergeToolTip));
    }

    // ------------------------------------------------------------------ suggested changes

    /// <summary>Suggestions gathered to commit together (GitHub's "Add suggestion to batch").</summary>
    public ObservableCollection<SuggestedChange> SuggestionBatch { get; } = [];

    public bool HasSuggestionBatch => SuggestionBatch.Count > 0;
    public string CommitBatchText => SuggestionBatch.Count == 1 ? "Commit suggestion (1)" : $"Commit suggestions ({SuggestionBatch.Count})";

    public event Action? BatchChanged;

    public string? SuggestionsBlockedReason
    {
        get
        {
            var summary = Pr.Details?.Summary ?? Summary;
            if (!IsPullRequest) return "Suggestions can only be committed to a pull request.";
            if (summary.State != PullRequestState.Open) return "The pull request is closed.";
            if (summary.IsCrossRepository) return "The pull request's branch is in another repository (a fork): commit the suggestion on GitHub.";
            if (Pr.HeadSha is null) return "The pull request's changes haven't loaded yet.";
            return null;
        }
    }

    // Suggestions are matched by what they'd do, so the same suggestion seen in two places (the diff and the
    // conversation tab) is one entry.
    public bool IsInBatch(SuggestedChange change) => SuggestionBatch.Contains(change, SuggestionComparer.Instance);

    public void ToggleInBatch(SuggestedChange change)
    {
        var existing = SuggestionBatch.FirstOrDefault(c => SuggestionComparer.Instance.Equals(c, change));
        if (existing is not null) SuggestionBatch.Remove(existing);
        else if (SuggestionBatch.Any(c => c.Path == change.Path && c.StartLine <= change.EndLine && change.StartLine <= c.EndLine))
        {
            ShowError("The batch already changes some of those lines. Commit it first, or take that suggestion out.");
            return;
        }
        else SuggestionBatch.Add(change);
        OnBatchChanged();
    }

    private void OnBatchChanged()
    {
        OnPropertyChanged(nameof(HasSuggestionBatch));
        OnPropertyChanged(nameof(CommitBatchText));
        BatchChanged?.Invoke();
    }

    [RelayCommand]
    private void ClearSuggestionBatch()
    {
        SuggestionBatch.Clear();
        OnBatchChanged();
    }

    public Task CommitSuggestionAsync(SuggestedChange change) => CommitSuggestionsAsync([change]);

    [RelayCommand]
    private Task CommitSuggestionBatchAsync() => CommitSuggestionsAsync([.. SuggestionBatch]);

    /// <summary>
    /// Commits suggestions onto the pull request's latest commit and pushes that to its branch, after asking for the
    /// commit message, then reloads. Nothing local changes: no branch moves and no worktree is touched.
    /// </summary>
    private async Task CommitSuggestionsAsync(IReadOnlyList<SuggestedChange> changes)
    {
        if (changes.Count == 0 || Dialogs is null || Provider is not { } provider || Pr.HeadSha is not { } head) return;
        if (SuggestionsBlockedReason is { } reason)
        {
            ShowError(reason);
            return;
        }
        var branch = (Pr.Details?.Summary ?? Summary).HeadRef;
        var remote = provider.Host.RemoteName;
        var message = new FormField(FormFieldKind.MultilineText, "Commit message") { Text = Suggestions.CommitMessage(changes, ViewerLogin), Height = 110 };
        var what = changes.Count == 1 ? "the suggestion" : $"{changes.Count} suggestions";
        if (!await Dialogs.ShowFormAsync(new FormSpec(changes.Count == 1 ? "Commit suggestion" : "Commit suggestions",
                $"Commits {what} on top of the pull request's latest commit and pushes it to {branch} on {remote}. Your own branches and worktrees aren't changed.",
                "Commit and push", [message], () => message.Text.Trim().Length == 0 ? "Write a commit message." : null)))
            return;

        IsBusy = true;
        BusyText = changes.Count == 1 ? "Committing the suggestion…" : "Committing suggestions…";
        try
        {
            var wd = _ctx.WorkingDirectory;
            var sha = await Suggestions.CommitAsync(wd, head, changes, message.Text.Replace("\r\n", "\n").Trim());
            await GitActions.PushCommitAsync(wd, remote, sha, branch);
            foreach (var c in changes)
                if (SuggestionBatch.FirstOrDefault(b => SuggestionComparer.Instance.Equals(b, c)) is { } inBatch) SuggestionBatch.Remove(inBatch);
            OnBatchChanged();
            ShowInfo($"Committed {what} as {sha[..7]} and pushed it to {branch}.");
            BusyText = "Waiting for GitHub…";
            await WaitForHeadAsync(provider, sha);
        }
        catch (GitCommandException ex) when (GitActions.ClassifyPushError(ex.Message) is not null)
        {
            ShowError($"{branch} changed on {remote} since the pull request was loaded, so the suggestion wasn't pushed. Reload (F5) and try again.");
            return;
        }
        catch (Exception ex) when (ex is GitCommandException or InvalidOperationException or IOException)
        {
            ShowError($"Couldn't commit {what}: {ex.Message}");
            return;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
        await LoadAsync();
    }

    /// <summary>The host moves the pull request's head a moment after a push: waits (up to ten seconds) until it has.</summary>
    private async Task WaitForHeadAsync(IPullRequestProvider provider, string sha)
    {
        for (var i = 0; i < 10; i++)
        {
            try
            {
                if ((await Task.Run(() => provider.GetPullRequestAsync(Number))).Summary.HeadSha == sha) return;
            }
            catch (HostException)
            {
                return;
            }
            await Task.Delay(1000);
        }
    }

    private sealed class SuggestionComparer : IEqualityComparer<SuggestedChange>
    {
        public static readonly SuggestionComparer Instance = new();

        public bool Equals(SuggestedChange? x, SuggestedChange? y) =>
            x is not null && y is not null && x.Path == y.Path && x.StartLine == y.StartLine && x.EndLine == y.EndLine
            && x.Replacement.SequenceEqual(y.Replacement);

        public int GetHashCode(SuggestedChange c) => HashCode.Combine(c.Path, c.StartLine, c.EndLine, c.Replacement.Count);
    }
}
