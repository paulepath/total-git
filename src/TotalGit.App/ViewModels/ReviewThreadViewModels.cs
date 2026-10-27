using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Git;
using TotalGit.Core.Hosting;

namespace TotalGit.App.ViewModels;

/// <summary>Something shown between diff lines: a thread, a comment being written, or a pending review comment.</summary>
/// <param name="LineIndex">The index into the diff's lines it's shown below.</param>
public sealed record DiffThreadItem(int LineIndex, object ViewModel, DiffSide Side);

/// <summary>What comments and threads need from the review window: the host's actions and a little of its state.</summary>
public interface ICommentHost
{
    PrCapabilities Capabilities { get; }

    /// <summary>The signed-in user's login (empty until known).</summary>
    string ViewerLogin { get; }

    /// <summary>The signed-in user, with their avatar, once known.</summary>
    PrUser? Viewer { get; }

    /// <summary>The pull request author's login (their comments get an "Author" badge).</summary>
    string PullRequestAuthor { get; }

    AvatarCache? Avatars { get; }

    Task<bool> ReplyAsync(ReviewThread thread, string body);
    Task<bool> SetResolvedAsync(ReviewThread thread, bool resolved);

    /// <summary>Adds or takes away a reaction. Doesn't reload: the comment shows the change at once.</summary>
    Task<bool> ReactAsync(PrComment comment, Reaction reaction, bool add);

    Task<bool> EditAsync(PrComment comment, string body);

    /// <summary>Deletes a comment after asking.</summary>
    Task<bool> DeleteAsync(PrComment comment);

    Task<bool> SetHiddenAsync(PrComment comment, HideReason? reason);
    Task<IReadOnlyList<CommentEdit>> GetEditsAsync(PrComment comment);
    Task<IReadOnlyList<PrUser>> SearchMentionsAsync(string query);
    Task CopyAsync(string text);

    /// <summary>Why suggested changes can't be committed from here, or null when they can.</summary>
    string? SuggestionsBlockedReason { get; }

    Task CommitSuggestionAsync(SuggestedChange change);
    bool IsInBatch(SuggestedChange change);
    void ToggleInBatch(SuggestedChange change);

    /// <summary>
    /// Raised when suggestions are added to or taken out of the batch, or it's committed, or whether they can be
    /// committed may have changed (the pull request reloaded).
    /// </summary>
    event Action? BatchChanged;
}

/// <summary>Someone on a pull request: their avatar (initials until it loads, or when there's none) and names.</summary>
public sealed partial class PersonViewModel : ObservableObject
{
    public PersonViewModel(PrUser user, AvatarCache? avatars)
    {
        User = user;
        Initials = Core.Avatars.AvatarIdentity.Initials(user.DisplayName);
        InitialsBrush = new SolidColorBrush(HslColor.FromHsl(Core.Avatars.AvatarIdentity.Hue(user.Login), 0.45, 0.42).ToRgb());
        if (user.AvatarUrl is { } url && avatars is not null) _ = LoadAsync(avatars, url);
    }

    public PrUser User { get; }
    public string Login => User.Login;
    public string Name => User.DisplayName;
    public string Initials { get; }
    public IBrush InitialsBrush { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar), nameof(Background))]
    public partial Bitmap? Avatar { get; private set; }

    public bool HasAvatar => Avatar is not null;

    /// <summary>The circle behind the initials; none behind a picture.</summary>
    public IBrush? Background => HasAvatar ? null : InitialsBrush;

    private async Task LoadAsync(AvatarCache avatars, string url) => Avatar = await avatars.GetUrlAsync(url);
}

/// <summary>A reaction to offer in the picker.</summary>
public sealed record ReactionChoice(Reaction Reaction, string Emoji, string Name)
{
    public static readonly IReadOnlyList<ReactionChoice> All =
    [
        new(Reaction.ThumbsUp, "👍", "thumbs up"),
        new(Reaction.ThumbsDown, "👎", "thumbs down"),
        new(Reaction.Laugh, "😄", "laugh"),
        new(Reaction.Hooray, "🎉", "hooray"),
        new(Reaction.Confused, "😕", "confused"),
        new(Reaction.Heart, "❤️", "heart"),
        new(Reaction.Rocket, "🚀", "rocket"),
        new(Reaction.Eyes, "👀", "eyes"),
    ];

    public static ReactionChoice Of(Reaction reaction) => All.First(c => c.Reaction == reaction);
}

/// <summary>A reaction in a comment's picker.</summary>
public sealed record ReactionPick(ReactionChoice Choice, bool IsMine, System.Windows.Input.ICommand Command)
{
    public string Emoji => Choice.Emoji;
    public Reaction Reaction => Choice.Reaction;
    public string ToolTip => (IsMine ? "Take away your " : "React with ") + Choice.Name;
}

/// <summary>One kind of reaction on a comment: the emoji and how many, filled in when one of them is yours.</summary>
public sealed class ReactionChipViewModel(ReactionGroup group, string viewerLogin, CommentViewModel comment)
{
    public Reaction Reaction => group.Reaction;
    public string Emoji => ReactionChoice.Of(group.Reaction).Emoji;
    public int Count => group.Count;
    public bool IsMine => group.ViewerReacted;
    public CommentViewModel Comment => comment;

    /// <summary>"You and Andy Scott reacted with thumbs up", naming up to a few people.</summary>
    public string ToolTip
    {
        get
        {
            var names = group.Reactors.Where(r => !string.Equals(r.Login, viewerLogin, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.DisplayName).ToList();
            if (group.ViewerReacted) names.Insert(0, "You");
            var others = group.Count - names.Count;
            if (others > 0) names.Add(others == 1 ? "1 other" : $"{others} others");
            var who = names.Count switch
            {
                0 => $"{group.Count} people",
                1 => names[0],
                _ => string.Join(", ", names[..^1]) + " and " + names[^1],
            };
            return $"{who} reacted with {ReactionChoice.Of(group.Reaction).Name}";
        }
    }
}

/// <summary>A version of a comment, for its edit history.</summary>
public sealed record EditEntry(string Who, string When, string What);

/// <summary>
/// The text box comments are written in: Write and Preview tabs, Markdown help (the toolbar and @mentions live in
/// the view, which owns the selection) and a suggested-change block for the lines being commented on.
/// </summary>
public sealed partial class CommentEditorViewModel : ObservableObject
{
    private readonly ICommentHost? _host;
    private readonly Func<IEnumerable<PrUser>> _participants;

    public CommentEditorViewModel(ICommentHost? host, string placeholder, IReadOnlyList<string>? suggestionLines, Func<IEnumerable<PrUser>> participants)
    {
        _host = host;
        Placeholder = placeholder;
        SuggestionLines = suggestionLines;
        _participants = participants;
    }

    public string Placeholder { get; }

    /// <summary>The lines a suggested change replaces (the commented lines); null when nothing can be suggested here.</summary>
    public IReadOnlyList<string>? SuggestionLines { get; }

    public bool CanSuggest => SuggestionLines is not null;

    public AvatarCache? Avatars => _host?.Avatars;

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWriting))]
    public partial bool IsPreview { get; set; }

    public bool IsWriting => !IsPreview;

    /// <summary>What Ctrl+Enter does (send the comment or reply).</summary>
    public System.Windows.Input.ICommand? SubmitCommand { get; set; }

    [RelayCommand]
    private void ShowWrite() => IsPreview = false;

    [RelayCommand]
    private void ShowPreview() => IsPreview = true;

    /// <summary>Raised to put the cursor in the box (a reply opened, a quote added).</summary>
    public event Action? FocusRequested;

    public void RequestFocus()
    {
        IsPreview = false;
        WantsFocus = true;
        FocusRequested?.Invoke();
    }

    /// <summary>The cursor was asked for and hasn't gone in yet (the box may not be on screen yet).</summary>
    public bool WantsFocus { get; set; }

    /// <summary>Adds a quote of another comment, then a blank line to write under it.</summary>
    public void InsertQuote(string body)
    {
        var quote = string.Join('\n', body.Replace("\r\n", "\n").Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));
        Text = (Text.Trim().Length > 0 ? Text.TrimEnd() + "\n\n" : "") + quote + "\n\n";
        RequestFocus();
    }

    /// <summary>People to offer for "@prefix": the conversation's people first, then the repository's.</summary>
    public async Task<IReadOnlyList<PrUser>> MentionsAsync(string prefix)
    {
        bool Matches(PrUser u) => u.Login.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                  || (u.Name?.Split(' ').Any(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ?? false);
        var people = _participants().Where(u => u.Login != "ghost" && Matches(u)).ToList();
        if (_host is { } host && host.Capabilities.HasFlag(PrCapabilities.Mentions) && prefix.Length > 0)
        {
            try
            {
                people.AddRange(await host.SearchMentionsAsync(prefix));
            }
            catch (HostException)
            {
                // Offline or rate limited: the conversation's people are still offered.
            }
        }
        return people.DistinctBy(u => u.Login, StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }
}

/// <summary>A suggested change in a comment: committed on its own, or gathered into a batch to commit together.</summary>
public sealed partial class SuggestionBlockViewModel : ObservableObject
{
    private readonly ICommentHost? _host;

    private readonly string? _threadReason;

    /// <param name="threadReason">Why the thread's suggestions can't apply at all (outdated…), when <paramref name="change"/> is null.</param>
    public SuggestionBlockViewModel(ICommentHost? host, SuggestedChange? change, string? threadReason)
    {
        _host = host;
        Change = change;
        _threadReason = threadReason;
    }

    public SuggestedChange? Change { get; }

    /// <summary>Why it can't be committed from here (outdated, a fork, merged, not loaded yet…); null when it can.</summary>
    public string? BlockedReason => Change is null ? _threadReason ?? "This suggestion can't be applied here." : _host?.SuggestionsBlockedReason;

    public bool CanApply => BlockedReason is null && _host is not null;
    public bool IsInBatch => Change is not null && _host?.IsInBatch(Change) == true;
    public string BatchText => IsInBatch ? "Remove from batch" : "Add suggestion to batch";

    /// <summary>The batch, or whether suggestions can be committed, changed: so may this block's buttons.</summary>
    public void BatchChanged() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private Task CommitAsync() => CanApply ? _host!.CommitSuggestionAsync(Change!) : Task.CompletedTask;

    [RelayCommand]
    private void ToggleBatch()
    {
        if (CanApply) _host!.ToggleInBatch(Change!);
    }
}

/// <summary>Where a Markdown view gets the buttons for a comment's suggested changes, and the lines they replace.</summary>
public interface ISuggestionSource
{
    /// <summary>The lines the comment's suggestions replace, when known.</summary>
    IReadOnlyList<string>? ReplacedLines { get; }

    /// <summary>The buttons for the comment's <paramref name="index"/>th suggestion.</summary>
    SuggestionBlockViewModel Block(int index, IReadOnlyList<string> lines);
}

/// <summary>One comment in a thread: who, when, its text, reactions, and what can be done with it.</summary>
public sealed partial class CommentViewModel : ObservableObject, ISuggestionSource
{
    private readonly ReviewThreadViewModel _thread;
    private readonly ICommentHost? _host;
    private readonly List<SuggestionBlockViewModel> _blocks = [];

    public CommentViewModel(PrComment comment, ReviewThreadViewModel thread, ICommentHost? host)
    {
        _thread = thread;
        _host = host;
        Comment = comment;
        Author = new PersonViewModel(comment.Author, host?.Avatars);
        Reactions = BuildReactions();
    }

    public PrComment Comment { get; private set; }
    public string Id => Comment.Id;
    public PersonViewModel Author { get; private set; }

    /// <summary>Takes a newer copy of the comment (after a reload), keeping what's being done with it.</summary>
    public void Update(PrComment comment)
    {
        if (comment == Comment) return;
        if (comment.Author.Login != Comment.Author.Login || comment.Author.AvatarUrl != Comment.Author.AvatarUrl)
        {
            Author = new PersonViewModel(comment.Author, _host?.Avatars);
            OnPropertyChanged(nameof(Author));
        }
        var bodyChanged = comment.Body != Comment.Body;
        Comment = comment;
        if (bodyChanged) _blocks.Clear();
        Reactions = BuildReactions();
        OnPropertyChanged(string.Empty);
    }

    public string AuthorName => Comment.Author.DisplayName;
    public string AuthorLogin => Comment.Author.Login;

    /// <summary>The login beside the name, when the name isn't just the login.</summary>
    public bool ShowLogin => AuthorName != AuthorLogin;

    public bool IsBot => Comment.Author.IsBot;
    public bool IsPullRequestAuthor => _host is { } h && string.Equals(Comment.Author.Login, h.PullRequestAuthor, StringComparison.OrdinalIgnoreCase);
    public bool IsMine => _host is { ViewerLogin.Length: > 0 } h && string.Equals(Comment.Author.Login, h.ViewerLogin, StringComparison.OrdinalIgnoreCase);

    public string? RoleText => Comment.Role switch
    {
        AuthorRole.Owner => "Owner",
        AuthorRole.Member => "Member",
        AuthorRole.Collaborator => "Collaborator",
        AuthorRole.Contributor => "Contributor",
        AuthorRole.FirstTimeContributor => "First-time contributor",
        _ => null,
    };
    public bool HasRole => RoleText is not null && !IsBot;

    public string When => DateText.Relative(Comment.CreatedAt);
    public string WhenToolTip => Comment.CreatedAt.LocalDateTime.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);
    public string Body => Comment.Body.Trim();

    public bool IsEdited => Comment.EditedAt is not null;

    /// <summary>In your review in progress: only you can see it until the review is submitted.</summary>
    public bool IsPending => Comment.IsPending;
    public string EditedToolTip => Comment.EditedAt is { } at ? $"Edited {DateText.Relative(at)}" : "";

    // ------------------------------------------------------------------ hidden

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolded), nameof(IsBodyVisible), nameof(ShowHiddenText))]
    public partial bool ShowHidden { get; set; }

    /// <summary>Hidden by someone, and not opened here: only a line saying so shows.</summary>
    public bool IsFolded => Comment.IsHidden && !ShowHidden;
    public bool IsBodyVisible => !IsFolded && !IsEditing;
    public string ShowHiddenText => ShowHidden ? "Hide comment" : "Show comment";

    public string HiddenText => Comment.HiddenReason switch
    {
        HideReason.Spam => "This comment was hidden as spam.",
        HideReason.Abuse => "This comment was hidden as abuse.",
        HideReason.OffTopic => "This comment was hidden as off-topic.",
        HideReason.Outdated => "This comment was hidden as outdated.",
        HideReason.Duplicate => "This comment was hidden as a duplicate.",
        HideReason.Resolved => "This comment was hidden as resolved.",
        _ => "This comment was hidden.",
    };

    [RelayCommand]
    private void ToggleShowHidden() => ShowHidden = !ShowHidden;

    // ------------------------------------------------------------------ what can be done

    private bool Has(PrCapabilities c) => _host?.Capabilities.HasFlag(c) == true;
    public bool CanReact => Has(PrCapabilities.Reactions) && Comment.ViewerCanReact;
    public bool CanEdit => Has(PrCapabilities.EditComments) && Comment.ViewerCanEdit;
    public bool CanDelete => Has(PrCapabilities.EditComments) && Comment.ViewerCanDelete;
    public bool CanHide => Has(PrCapabilities.HideComments) && Comment.ViewerCanHide && !Comment.IsHidden;
    public bool CanUnhide => Has(PrCapabilities.HideComments) && Comment.ViewerCanHide && Comment.IsHidden;
    public bool CanQuote => _thread.CanReply;
    public bool HasUrl => Comment.Url is not null;
    public bool CanSeeEdits => IsEdited && Has(PrCapabilities.EditComments);

    /// <summary>Anything for the "…" menu's lower part (edit, hide, delete).</summary>
    public bool CanManage => CanEdit || CanDelete || CanHide || CanUnhide;

    [RelayCommand]
    private void QuoteReply() => _thread.QuoteReply(Body);

    [RelayCommand]
    private Task CopyLinkAsync() => Comment.Url is { } url && _host is not null ? _host.CopyAsync(url) : Task.CompletedTask;

    [RelayCommand]
    private void OpenOnWeb()
    {
        if (Comment.Url is { } url) UrlLauncher.Open(url);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_host is not null) await _host.DeleteAsync(Comment);
    }

    [RelayCommand]
    private async Task HideAsync(HideReason reason)
    {
        if (_host is not null) await _host.SetHiddenAsync(Comment, reason);
    }

    [RelayCommand]
    private async Task UnhideAsync()
    {
        if (_host is not null) await _host.SetHiddenAsync(Comment, null);
    }

    // ------------------------------------------------------------------ editing

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBodyVisible))]
    public partial bool IsEditing { get; private set; }

    [ObservableProperty]
    public partial CommentEditorViewModel? Editor { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand))]
    public partial bool IsSaving { get; private set; }

    [RelayCommand]
    private void StartEdit()
    {
        var editor = new CommentEditorViewModel(_host, "Edit the comment", _thread.SuggestionLines, _thread.People) { Text = Comment.Body };
        editor.SubmitCommand = SaveEditCommand;
        Editor = editor;
        IsEditing = true;
        editor.RequestFocus();
    }

    private bool CanSaveEdit() => !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSaveEdit))]
    private async Task SaveEditAsync()
    {
        if (_host is null || Editor is not { } editor || editor.Text.Trim().Length == 0) return;
        IsSaving = true;
        try
        {
            if (await _host.EditAsync(Comment, editor.Text.Trim())) CancelEdit();
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        Editor = null;
    }

    /// <summary>The comment's versions, read when its "edited" menu opens.</summary>
    public ObservableCollection<EditEntry> EditHistory { get; } = [];

    [RelayCommand]
    private async Task LoadEditsAsync()
    {
        if (_host is null) return;
        EditHistory.Clear();
        EditHistory.Add(new EditEntry("Loading…", "", ""));
        IReadOnlyList<CommentEdit> edits;
        try
        {
            edits = await _host.GetEditsAsync(Comment);
        }
        catch (HostException ex)
        {
            EditHistory.Clear();
            EditHistory.Add(new EditEntry(ex.Message, "", ""));
            return;
        }
        EditHistory.Clear();
        for (var i = 0; i < edits.Count; i++)
            EditHistory.Add(new EditEntry(edits[i].Editor.DisplayName, DateText.Relative(edits[i].At), i == edits.Count - 1 ? "created" : "edited"));
    }

    // ------------------------------------------------------------------ reactions

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReactions))]
    public partial IReadOnlyList<ReactionChipViewModel> Reactions { get; private set; }

    public bool HasReactions => Reactions.Count > 0;

    /// <summary>The picker's eight reactions, yours marked.</summary>
    public IReadOnlyList<ReactionPick> ReactionPicks => ReactionChoice.All
        .Select(c => new ReactionPick(c, Comment.Reactions.Any(r => r.Reaction == c.Reaction && r.ViewerReacted), ToggleReactionCommand))
        .ToList();

    private List<ReactionChipViewModel> BuildReactions() =>
        Comment.Reactions.Select(g => new ReactionChipViewModel(g, _host?.ViewerLogin ?? "", this)).ToList();

    /// <summary>Adds the reaction, or takes it away if it's already yours. Shown at once; put back if the host refuses.</summary>
    [RelayCommand]
    private async Task ToggleReactionAsync(Reaction reaction)
    {
        if (_host is null || !CanReact) return;
        var before = Comment;
        var existing = before.Reactions.FirstOrDefault(r => r.Reaction == reaction);
        var add = existing is not { ViewerReacted: true };
        var me = new PrUser(_host.ViewerLogin, null);
        var groups = before.Reactions.Where(r => r.Reaction != reaction).ToList();
        if (add) groups.Add(existing is null ? new ReactionGroup(reaction, 1, true, [me]) : existing with { Count = existing.Count + 1, ViewerReacted = true });
        else if (existing!.Count > 1) groups.Add(existing with { Count = existing.Count - 1, ViewerReacted = false, Reactors = existing.Reactors.Where(r => r.Login != me.Login).ToList() });
        Update(before with { Reactions = groups.OrderBy(g => g.Reaction).ToList() });
        if (!await _host.ReactAsync(before, reaction, add)) Update(before);
    }

    // ------------------------------------------------------------------ suggested changes

    public IReadOnlyList<string>? ReplacedLines => _thread.SuggestionLines;

    public SuggestionBlockViewModel Block(int index, IReadOnlyList<string> lines)
    {
        while (_blocks.Count <= index) _blocks.Add(null!);
        if (_blocks[index] is { } block) return block;
        var change = _thread.SuggestedChange(lines, Comment.Author);
        return _blocks[index] = new SuggestionBlockViewModel(_host, change, _thread.SuggestionBlockedReason);
    }

    /// <summary>The batch changed: refresh the suggestion buttons.</summary>
    public void BatchChanged()
    {
        foreach (var b in _blocks) b?.BatchChanged();
    }
}

/// <summary>A line of the diff an outdated comment was made on.</summary>
public sealed record HunkLine(string Text, IBrush Background);

/// <summary>A review thread: its comments, who's in it, a reply box, and resolve / unresolve.</summary>
public sealed partial class ReviewThreadViewModel : ObservableObject
{
    private static readonly IBrush AddedBrush = Palette.AddedLine;
    private static readonly IBrush RemovedBrush = Palette.RemovedLine;

    private readonly ICommentHost? _host;

    public ReviewThreadViewModel(ReviewThread thread, ICommentHost? host)
    {
        Thread = thread;
        _host = host;
        foreach (var c in thread.Comments) Comments.Add(new CommentViewModel(c, this, host));
        // Resolved conversations start folded, as on GitHub.
        IsExpanded = !thread.IsResolved;
        if (host is not null) host.BatchChanged += OnBatchChanged;
    }

    public ReviewThread Thread { get; private set; }
    public ObservableCollection<CommentViewModel> Comments { get; } = [];

    /// <summary>Takes a newer copy of the thread (after a reload), keeping what's open and being written.</summary>
    public void Update(ReviewThread thread)
    {
        if (thread == Thread) return;
        Thread = thread;
        var byId = thread.Comments.ToDictionary(c => c.Id);
        for (var i = Comments.Count - 1; i >= 0; i--)
            if (!byId.ContainsKey(Comments[i].Id)) Comments.RemoveAt(i);
        for (var i = 0; i < thread.Comments.Count; i++)
        {
            var c = thread.Comments[i];
            var existing = Comments.FirstOrDefault(vm => vm.Id == c.Id);
            if (existing is null) Comments.Insert(Math.Min(i, Comments.Count), new CommentViewModel(c, this, _host));
            else existing.Update(c);
        }
        OnPropertyChanged(string.Empty);
    }

    private void OnBatchChanged()
    {
        foreach (var c in Comments) c.BatchChanged();
    }

    /// <summary>Stops listening to the host (the thread is gone).</summary>
    public void Detach()
    {
        if (_host is not null) _host.BatchChanged -= OnBatchChanged;
    }

    public bool IsResolved => Thread.IsResolved;
    public bool IsOpen => !Thread.IsResolved;
    public bool IsOutdated => Thread.IsOutdated;
    public bool CanReply => Thread.ViewerCanReply && _host is not null;
    public bool CanResolve => Thread.ViewerCanResolve && _host is not null;
    public string ResolveText => IsResolved ? "Unresolve" : "Resolve";
    public string ResolveToolTip => IsResolved ? "Open the conversation again" : "Mark the conversation as resolved";

    // ------------------------------------------------------------------ the header

    public string FilePath => Thread.Anchor.Path;
    public string FileName => Path.GetFileName(Thread.Anchor.Path);

    /// <summary>"lines 173–187", "line 12", "old line 4", or for an outdated comment the line it was on.</summary>
    public string LinesText
    {
        get
        {
            var a = Thread.Anchor;
            var side = a.Side == DiffSide.Left ? "old " : "";
            if (a.Line is { } line)
                return a.StartLine is { } start ? $"{side}lines {start}–{line}" : $"{side}line {line}";
            return a.OriginalLine is { } original ? $"was {side}line {original}" : "";
        }
    }

    /// <summary>Where the thread is, for lists outside the diff ("src/app.py:12").</summary>
    public string Location => Thread.Anchor.Line is { } line ? $"{Thread.Anchor.Path}:{line}"
        : Thread.Anchor.OriginalLine is { } original ? $"{Thread.Anchor.Path}:{original}" : Thread.Anchor.Path;

    /// <summary>Everyone who has commented, once each, in order (up to four avatars).</summary>
    public IReadOnlyList<PersonViewModel> Participants => Comments.Select(c => c.Author).DistinctBy(a => a.Login).Take(4).ToList();

    public string ParticipantsToolTip => string.Join(", ", Comments.Select(c => c.AuthorName).Distinct());

    /// <summary>The conversation's people, offered first for @mentions.</summary>
    public IEnumerable<PrUser> People() => Thread.Comments.Select(c => c.Author);

    public int CommentCount => Comments.Count;
    public string CountToolTip => CommentCount == 1 ? "1 comment" : $"{CommentCount} comments";

    /// <summary>The first comment's title, shown while the thread is folded.</summary>
    public string Title => Thread.Comments.FirstOrDefault() is { } first ? CommentText.Title(first.Body) : "";

    public string LastReplyText => Thread.Comments.LastOrDefault() is { } last
        ? (Thread.Comments.Count == 1 ? "From " : "Last reply from ") + last.Author.DisplayName + ", " + DateText.Relative(last.CreatedAt)
        : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolded))]
    public partial bool IsExpanded { get; set; }

    public bool IsFolded => !IsExpanded;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>"Andy Scott marked this conversation as resolved".</summary>
    public string ResolvedText => Thread.ResolvedBy is { } by ? $"{by.DisplayName} marked this conversation as resolved" : "This conversation was marked as resolved";

    // ------------------------------------------------------------------ outdated: the code it was made on

    public bool HasOriginalCode => IsOutdated && OriginalCode.Count > 0;

    [ObservableProperty]
    public partial bool ShowOriginalCode { get; set; }

    public string OriginalCodeText => ShowOriginalCode ? "Hide the code it was made on" : "Show the code it was made on";

    partial void OnShowOriginalCodeChanged(bool value) => OnPropertyChanged(nameof(OriginalCodeText));

    [RelayCommand]
    private void ToggleOriginalCode() => ShowOriginalCode = !ShowOriginalCode;

    /// <summary>The end of the diff the first comment was made on (where it pointed).</summary>
    public IReadOnlyList<HunkLine> OriginalCode => Thread.DiffHunk is { } hunk
        ? hunk.Replace("\r\n", "\n").Split('\n').Where(l => !l.StartsWith("@@", StringComparison.Ordinal)).TakeLast(8)
            .Select(l => new HunkLine(l, l.StartsWith('+') ? AddedBrush : l.StartsWith('-') ? RemovedBrush : Brushes.Transparent)).ToList()
        : [];

    // ------------------------------------------------------------------ suggested changes

    private int? SuggestionLineCount => Thread.Anchor is { Side: DiffSide.Right, Line: { } line } a && !IsOutdated ? line - (a.StartLine ?? line) + 1 : null;

    /// <summary>The commented lines (what a suggested change replaces), from the diff the thread was made on.</summary>
    public IReadOnlyList<string>? SuggestionLines => SuggestionLineCount is { } n ? Suggestions.ReplacedLines(Thread.DiffHunk, n) : null;

    /// <summary>Why suggestions on this thread can't be committed, whatever the pull request.</summary>
    public string SuggestionBlockedReason =>
        IsOutdated ? "The lines changed since this suggestion was made."
        : Thread.Anchor.Side == DiffSide.Left ? "This suggestion is on removed lines."
        : "This suggestion can't be applied here.";

    /// <summary>A suggestion made in this thread, as a change to the commented lines; null when it can't apply.</summary>
    public SuggestedChange? SuggestedChange(IReadOnlyList<string> replacement, PrUser author) =>
        Thread.Anchor is { Side: DiffSide.Right, Line: { } line } a && !IsOutdated
            ? new SuggestedChange(a.Path, a.StartLine ?? line, line, replacement, author)
            : null;

    // ------------------------------------------------------------------ replying and resolving

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReplyClosed))]
    public partial bool IsReplyOpen { get; private set; }

    public bool IsReplyClosed => !IsReplyOpen;

    /// <summary>The signed-in user, beside the reply box.</summary>
    public PersonViewModel? Viewer => _viewer ??= _host?.Viewer is { } me ? new PersonViewModel(me, _host.Avatars) : null;
    private PersonViewModel? _viewer;
    public bool HasViewer => Viewer is not null;

    [ObservableProperty]
    public partial CommentEditorViewModel? ReplyEditor { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplyCommand), nameof(ReplyAndResolveCommand), nameof(ToggleResolvedCommand))]
    public partial bool IsBusy { get; set; }

    public string ReplyAndResolveText => IsResolved ? "Reply and reopen" : "Reply and resolve";

    [RelayCommand]
    private void StartReply()
    {
        if (!CanReply) return;
        if (ReplyEditor is null)
        {
            var editor = new CommentEditorViewModel(_host, "Reply… (Markdown, @mentions; Ctrl+Enter sends)", SuggestionLines, People);
            editor.SubmitCommand = ReplyCommand;
            ReplyEditor = editor;
        }
        IsReplyOpen = true;
        ReplyEditor.RequestFocus();
    }

    [RelayCommand]
    private void CancelReply()
    {
        IsReplyOpen = false;
        ReplyEditor = null;
    }

    /// <summary>Quote reply: opens the thread and its reply box with the comment quoted.</summary>
    public void QuoteReply(string body)
    {
        if (!CanReply) return;
        IsExpanded = true;
        StartReply();
        ReplyEditor!.InsertQuote(body);
    }

    private bool CanSend() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task ReplyAsync() => SendAsync(andToggleResolved: false);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task ReplyAndResolveAsync() => SendAsync(andToggleResolved: true);

    private async Task SendAsync(bool andToggleResolved)
    {
        if (_host is null || ReplyEditor is not { } editor || editor.Text.Trim().Length == 0) return;
        IsBusy = true;
        try
        {
            var thread = Thread;
            if (!await _host.ReplyAsync(thread, editor.Text.Trim())) return;
            CancelReply();
            if (andToggleResolved && thread.ViewerCanResolve) await _host.SetResolvedAsync(thread, !thread.IsResolved);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task ToggleResolvedAsync()
    {
        if (_host is null) return;
        IsBusy = true;
        try
        {
            // Resolving folds the conversation, as on GitHub.
            if (await _host.SetResolvedAsync(Thread, !IsResolved) && !IsResolved) IsExpanded = false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>A new line comment being written: post it now, or add it to a review to send later.</summary>
public sealed partial class CommentComposerViewModel : ObservableObject
{
    private readonly Func<CommentComposerViewModel, Task<bool>> _postNow;
    private readonly Action<CommentComposerViewModel> _addToReview;
    private readonly Action<CommentComposerViewModel> _cancel;

    public CommentComposerViewModel(CommentAnchor anchor, int lineIndex, int pendingCount,
        Func<CommentComposerViewModel, Task<bool>> postNow, Action<CommentComposerViewModel> addToReview, Action<CommentComposerViewModel> cancel,
        ICommentHost? host = null, IReadOnlyList<string>? suggestionLines = null, PrUser? viewer = null)
    {
        Anchor = anchor;
        LineIndex = lineIndex;
        PendingCount = pendingCount;
        _postNow = postNow;
        _addToReview = addToReview;
        _cancel = cancel;
        Editor = new CommentEditorViewModel(host, "Leave a comment (Markdown, @mentions; Ctrl+Enter starts a review)", suggestionLines, () => []);
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(CommentEditorViewModel.Text)) return;
            PostNowCommand.NotifyCanExecuteChanged();
            AddToReviewCommand.NotifyCanExecuteChanged();
        };
        Editor.SubmitCommand = AddToReviewCommand;
        // A new comment box takes the keyboard straight away.
        Editor.RequestFocus();
        Viewer = viewer is null ? null : new PersonViewModel(viewer, host?.Avatars);
    }

    public CommentAnchor Anchor { get; }
    public int LineIndex { get; }
    public int PendingCount { get; }
    public CommentEditorViewModel Editor { get; }
    public PersonViewModel? Viewer { get; }
    public bool HasViewer => Viewer is not null;

    public string Text
    {
        get => Editor.Text;
        set => Editor.Text = value;
    }

    public string Title => $"Comment on {(Anchor.Side == DiffSide.Left ? "old " : "")}line {Anchor.Line}";

    /// <summary>"Start a review" at first, "Add to review" once comments are waiting.</summary>
    public string AddToReviewText => PendingCount == 0 ? "Start a review" : $"Add to review ({PendingCount} pending)";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PostNowCommand), nameof(AddToReviewCommand))]
    public partial bool IsBusy { get; set; }

    private bool HasText() => !IsBusy && Text.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(HasText))]
    private async Task PostNowAsync()
    {
        IsBusy = true;
        try { await _postNow(this); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(HasText))]
    private void AddToReview() => _addToReview(this);

    [RelayCommand]
    private void Cancel() => _cancel(this);

    /// <summary>
    /// The anchor for the diff line a comment is started on: the old side for a removed line, else the new side.
    /// Null for lines that can't take comments (hunk headers).
    /// </summary>
    public static CommentAnchor? AnchorFor(string path, DiffLine line) => line switch
    {
        { Kind: DiffLineKind.Removed, OldLine: { } old } => new CommentAnchor(path, DiffSide.Left, old),
        { Kind: DiffLineKind.Added or DiffLineKind.Context, NewLine: { } n } => new CommentAnchor(path, DiffSide.Right, n),
        _ => null,
    };
}

/// <summary>A line comment added to the review being written: sent when the review is submitted; can be changed until then.</summary>
public sealed partial class PendingCommentViewModel : ObservableObject
{
    private readonly Action<PendingCommentViewModel> _remove;
    private readonly Action<PendingCommentViewModel, DraftComment> _replace;

    public PendingCommentViewModel(DraftComment draft, Action<PendingCommentViewModel> remove, Action<PendingCommentViewModel, DraftComment>? replace = null,
        PersonViewModel? viewer = null, int pendingCount = 1)
    {
        Draft = draft;
        _remove = remove;
        _replace = replace ?? ((_, _) => { });
        Viewer = viewer;
        PendingCount = pendingCount;
    }

    public DraftComment Draft { get; private set; }
    public string Body => Draft.Body;
    public string Location => $"{Draft.Anchor.Path}:{Draft.Anchor.Line}";
    public PersonViewModel? Viewer { get; }
    public bool HasViewer => Viewer is not null;
    public string ViewerName => Viewer?.Name ?? "You";
    public int PendingCount { get; }
    public string PendingText => PendingCount == 1 ? "Sent when you submit your review." : $"Sent when you submit your review ({PendingCount} pending).";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowing))]
    public partial bool IsEditing { get; private set; }

    public bool IsShowing => !IsEditing;

    [ObservableProperty]
    public partial CommentEditorViewModel? Editor { get; private set; }

    [RelayCommand]
    private void Remove() => _remove(this);

    [RelayCommand]
    private void StartEdit()
    {
        Editor = new CommentEditorViewModel(null, "Edit the comment", null, () => []) { Text = Draft.Body };
        Editor.SubmitCommand = SaveEditCommand;
        IsEditing = true;
        Editor.RequestFocus();
    }

    [RelayCommand]
    private void SaveEdit()
    {
        if (Editor is not { } editor || editor.Text.Trim().Length == 0) return;
        Draft = Draft with { Body = editor.Text.Trim() };
        OnPropertyChanged(nameof(Body));
        CancelEdit();
        _replace(this, Draft);
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        Editor = null;
    }
}
