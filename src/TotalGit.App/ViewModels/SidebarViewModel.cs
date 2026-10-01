using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TotalGit.App.Services;
using TotalGit.Core.Git;
using TotalGit.Core.Hosting;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

public enum SidebarNodeKind
{
    Section,
    Folder,
    LocalBranch,
    RemoteBranch,
    Tag,
    Worktree,
    Stash,
    PullRequest,
    /// <summary>A note in place of rows, e.g. "Loading…" or why pull requests can't be shown.</summary>
    Message,
}

public partial class SidebarNode : ObservableObject
{
    public SidebarNode(SidebarNodeKind kind, string label)
    {
        Kind = kind;
        Label = label;
    }

    public SidebarNodeKind Kind { get; }
    public string Label { get; }
    public ObservableCollection<SidebarNode> Children { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCurrentDot))]
    public partial bool IsExpanded { get; set; }

    /// <summary>A folder or section with the current branch somewhere below it.</summary>
    public bool ContainsCurrent { get; set; }

    /// <summary>Shows where the current branch is while its folder is collapsed.</summary>
    public bool ShowCurrentDot => ContainsCurrent && !IsExpanded;

    public BranchTarget? Target { get; init; }
    public WorktreeInfo? Worktree { get; init; }
    public StashInfo? Stash { get; init; }

    /// <summary>Set for a folder under .worktrees that git no longer tracks.</summary>
    public string? LeftoverPath { get; init; }

    /// <summary>For a folder under LOCAL: the name prefix of branches inside it ("feature/", "feature/team/").</summary>
    public string? BranchPrefix { get; init; }

    /// <summary>Remembers whether a folder (or a branch other branches are grouped under) was collapsed.</summary>
    public string? FolderKey { get; set; }
    public bool IsCurrent { get; init; }
    public int Count { get; init; }
    public string? Ahead { get; init; }
    public string? Behind { get; init; }
    public string? ToolTip { get; init; }
    public string? Subtitle { get; init; }
    public bool HasSubtitle => Subtitle is not null;
    public bool HasWorktree { get; init; }
    public bool IsDimmed { get; init; }

    /// <summary>feature/bug/hot-fix icon, shown instead of the plain branch/folder/worktree glyph.</summary>
    public Bitmap? KindIcon { get; init; }
    public bool HasKindIcon => KindIcon is not null;
    public bool ShowBranchGlyph => IsBranch && KindIcon is null;
    public bool ShowFolderGlyph => IsFolder && KindIcon is null && !IsPrGroup;
    public bool ShowWorktreeGlyph => IsWorktree && KindIcon is null;

    public bool IsSection => Kind == SidebarNodeKind.Section;
    public bool IsFolder => Kind == SidebarNodeKind.Folder;
    public bool IsBranch => Kind is SidebarNodeKind.LocalBranch or SidebarNodeKind.RemoteBranch;
    public bool IsTag => Kind == SidebarNodeKind.Tag;
    public bool IsWorktree => Kind == SidebarNodeKind.Worktree;
    public bool IsStash => Kind == SidebarNodeKind.Stash;
    public bool IsWorktreesSection { get; init; }
    public bool IsPullRequestsSection { get; init; }
    public bool IsPullRequest => Kind == SidebarNodeKind.PullRequest;
    public PullRequestSummary? PullRequest { get; init; }

    /// <summary>A pull request's author, shown first on its row.</summary>
    public PrPerson? Author { get; init; }

    /// <summary>The first few people a pull request is for (asked to review, or who reviewed), shown at the end of its row.</summary>
    public IReadOnlyList<PrPerson> Reviewers { get; init; } = [];
    public bool HasReviewers => Reviewers.Count > 0;

    /// <summary>"+2" when there are more reviewers than fit.</summary>
    public string? MoreReviewers { get; init; }
    public bool HasMoreReviewers => MoreReviewers is not null;

    /// <summary>A pull request's hover card.</summary>
    public PrCardViewModel? Card { get; init; }

    /// <summary>What the row shows on hover: a pull request's card, otherwise the plain tooltip text.</summary>
    public object? Tip => (object?)Card ?? ToolTip;

    /// <summary>The colour at the left of a pull request's row: where it stands (see <see cref="PrRail"/>).</summary>
    public IBrush? RailBrush { get; init; }
    public bool HasRail => RailBrush is not null;

    /// <summary>"#45" on a pull request row.</summary>
    public string? NumberText { get; init; }

    /// <summary>The issue-tracker ticket a pull request is for ("E4-2361"), shown as a link.</summary>
    public string? Ticket { get; init; }
    public bool HasTicket => Ticket is not null;

    /// <summary>A heading inside PULL REQUESTS ("Needs your review").</summary>
    public bool IsPrGroup { get; init; }

    /// <summary>A pull request's checks: green passed, red failed, amber running.</summary>
    public IBrush? ChecksBrush => PullRequest?.Checks switch
    {
        ChecksState.Success => PrColors.Success,
        ChecksState.Failure => PrColors.Failure,
        ChecksState.Pending => PrColors.Pending,
        _ => null,
    };
    public bool HasChecks => ChecksBrush is not null;
    public bool IsApproved => PullRequest?.ReviewDecision == ReviewDecision.Approved;
    public bool IsChangesRequested => PullRequest?.ReviewDecision == ReviewDecision.ChangesRequested;

    /// <summary>You're asked to review this pull request.</summary>
    public bool IsReviewRequested => PullRequest?.ViewerReviewRequested == true;
    public bool ShowCount => IsSection || IsPrGroup;
    public bool HasAhead => Ahead is not null;
    public bool HasBehind => Behind is not null;

    /// <summary>The branch's remote branch was deleted (shown as a ✕ instead of ahead/behind).</summary>
    public bool IsUpstreamGone { get; init; }
}

/// <summary>Someone on a pull request row: their avatar (or initials until it loads) and, for a reviewer, their review.</summary>
public sealed partial class PrPerson : ObservableObject
{
    public PrPerson(string name, string? avatarUrl, string toolTip, ReviewState state = ReviewState.None)
    {
        Name = name;
        AvatarUrl = avatarUrl;
        ToolTip = toolTip;
        State = state;
        Initials = Core.Avatars.AvatarIdentity.Initials(name);
        InitialsBrush = new SolidColorBrush(HslColor.FromHsl(Core.Avatars.AvatarIdentity.Hue(name), 0.45, 0.42).ToRgb());
    }

    public string Name { get; }
    public string? AvatarUrl { get; }
    public string ToolTip { get; }
    public ReviewState State { get; }
    public string Initials { get; }
    public IBrush InitialsBrush { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar))]
    public partial Bitmap? Avatar { get; set; }

    public bool HasAvatar => Avatar is not null;

    /// <summary>The dot on a reviewer's avatar: green approved, red changes requested, grey still to review.</summary>
    public IBrush? StateBrush => State switch
    {
        ReviewState.Approved => PrColors.Success,
        ReviewState.ChangesRequested => PrColors.Failure,
        ReviewState.Commented => PendingBrush,
        ReviewState.None or ReviewState.Pending => PendingBrush,
        _ => null,
    };

    public bool HasState => StateBrush is not null;

    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#8A9099"));

    public static string Describe(Reviewer r) => r.State switch
    {
        ReviewState.Approved => $"{r.Name} approved",
        ReviewState.ChangesRequested => $"{r.Name} requested changes",
        ReviewState.Commented => r.IsRequested ? $"{r.Name} commented (review still requested)" : $"{r.Name} commented",
        ReviewState.Dismissed => $"{r.Name}'s review was dismissed",
        _ => $"{r.Name}: review requested",
    };
}

/// <summary>GitKraken-style left panel: LOCAL, REMOTE, TAGS and WORKTREES with a filter.</summary>
public partial class SidebarViewModel : ObservableObject
{
    private IReadOnlyList<RefInfo> _refs = [];
    private IReadOnlyList<WorktreeInfo> _worktrees = [];
    private IReadOnlyList<string> _leftovers = [];
    private IReadOnlyList<StashInfo> _stashes = [];
    private string? _currentWorktree;
    private readonly HashSet<string> _collapsed = ["REMOTE", "TAGS", "STASHES", "PULL REQUESTS/" + nameof(PrGroup.Drafts)];

    // Pull requests come from the hosting service, separately from the git refresh.
    private bool _showPullRequests;
    private IReadOnlyList<PullRequestSummary> _pullRequests = [];
    private string? _pullRequestsMessage;

    public ObservableCollection<SidebarNode> Nodes { get; } = [];

    /// <summary>Avatars for pull request authors and reviewers; set by the repository view model.</summary>
    public AvatarCache? Avatars
    {
        get => _avatars;
        set
        {
            if (_avatars is not null) _avatars.Updated -= RefreshAvatars;
            _avatars = value;
            if (_avatars is not null) _avatars.Updated += RefreshAvatars;
        }
    }

    private AvatarCache? _avatars;

    /// <summary>Fills in avatars that have loaded since the rows were built (without rebuilding the tree).</summary>
    private void RefreshAvatars()
    {
        foreach (var node in Flatten(Nodes.Where(n => n.IsPullRequestsSection)).Where(n => n.IsPullRequest))
            foreach (var person in (node.Card?.Reviewers.Select(r => r.Person) ?? node.Reviewers).Prepend(node.Author))
                if (person is { Avatar: null, AvatarUrl: { } url }) person.Avatar = _avatars?.TryGetUrl(url);
    }

    private PrPerson Person(string name, string? url, string toolTip, ReviewState state = ReviewState.None) =>
        new(name, url, toolTip, state) { Avatar = url is null ? null : _avatars?.TryGetUrl(url) };

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial SidebarNode? SelectedNode { get; set; }

    partial void OnFilterChanged(string value) => Rebuild();

    /// <summary>Rebuilds the rows after the branch rules (grouping, icons) changed.</summary>
    public void RefreshRules() => Rebuild();

    public void Update(IReadOnlyList<RefInfo> refs, IReadOnlyList<StashInfo> stashes, IReadOnlyList<WorktreeInfo> worktrees,
        IReadOnlyList<string> leftovers, string currentWorktree)
    {
        _refs = refs;
        _stashes = stashes;
        _worktrees = worktrees;
        _leftovers = leftovers;
        _currentWorktree = currentWorktree;
        Rebuild();
    }

    /// <summary>
    /// Shows the PULL REQUESTS section (hidden when <paramref name="visible"/> is false, e.g. the remote isn't on
    /// a supported host). <paramref name="message"/> replaces the rows: "Loading…" or why they can't be shown.
    /// </summary>
    public void SetPullRequests(bool visible, IReadOnlyList<PullRequestSummary> pullRequests, string? message)
    {
        _showPullRequests = visible;
        _pullRequests = pullRequests;
        _pullRequestsMessage = message;
        Rebuild();
    }

    public void Clear()
    {
        _showPullRequests = false;
        _pullRequests = [];
        _pullRequestsMessage = null;
        _refs = [];
        _stashes = [];
        _worktrees = [];
        _leftovers = [];
        Nodes.Clear();
    }

    private void Rebuild()
    {
        // Remember which folders/sections the user collapsed or expanded.
        foreach (var n in Flatten(Nodes).Where(n => n.IsSection || n.IsFolder || n.Children.Count > 0))
        {
            if (n.IsExpanded) _collapsed.Remove(KeyOf(n));
            else _collapsed.Add(KeyOf(n));
        }
        Nodes.Clear();

        var filter = Filter.Trim();
        bool Match(string s) => filter.Length == 0 || s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var worktreeByBranch = _worktrees.Where(w => w.Branch is not null).GroupBy(w => w.Branch!).ToDictionary(g => g.Key, g => g.First());

        var locals = _refs.Where(r => r.Kind == RefKind.LocalBranch && Match(r.Name)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var local = Section("LOCAL", locals.Count);
        var localTree = new BranchTree(this, local, remote: null);
        foreach (var r in locals)
        {
            worktreeByBranch.TryGetValue(r.Name, out var wt);
            var otherWorktree = wt is not null && !WorktreeService.SamePath(wt.Path, _currentWorktree ?? "") ? wt : null;
            localTree.Add(r.Name, leaf => new SidebarNode(SidebarNodeKind.LocalBranch, leaf)
            {
                KindIcon = BranchIcons.ForBranch(r.Name),
                Target = BranchTarget.From(r, wt),
                IsCurrent = r.IsCurrent,
                Ahead = r.Ahead > 0 ? $"{r.Ahead}↑" : null,
                Behind = r.Behind > 0 ? $"{r.Behind}↓" : null,
                IsUpstreamGone = r.UpstreamGone,
                HasWorktree = otherWorktree is not null,
                ToolTip = r.Name
                    + (otherWorktree is not null ? $"\nChecked out in worktree {otherWorktree.Path}" : "")
                    + (r.UpstreamGone ? $"\n{r.Upstream} was deleted on the remote" : ""),
            });
        }
        localTree.Build();
        Nodes.Add(local);

        var remotes = _refs.Where(r => r.Kind == RefKind.RemoteBranch && Match(r.Name)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var remote = Section("REMOTE", remotes.Count);
        foreach (var group in remotes.GroupBy(r => r.Name[..Math.Max(0, r.Name.Length - r.ShortName.Length - 1)]))
        {
            // Remote names start with the remote ("origin/feature/x"): the rules apply to the rest, under the remote's folder.
            var remoteTree = new BranchTree(this, remote, group.Key);
            foreach (var r in group)
            {
                remoteTree.Add(r.ShortName, leaf => new SidebarNode(SidebarNodeKind.RemoteBranch, leaf)
                {
                    KindIcon = BranchIcons.ForBranch(r.ShortName),
                    Target = BranchTarget.From(r, null),
                    ToolTip = r.Name,
                });
            }
            remoteTree.Build();
        }
        Nodes.Add(remote);

        var tags = _refs.Where(r => r.Kind == RefKind.Tag && Match(r.Name)).OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var tagSection = Section("TAGS", tags.Count);
        foreach (var r in tags)
            tagSection.Children.Add(new SidebarNode(SidebarNodeKind.Tag, r.Name) { Target = BranchTarget.From(r, null), ToolTip = r.Name });
        Nodes.Add(tagSection);

        var stashes = _stashes.Where(s => Match(s.Message) || Match(s.Branch ?? "")).ToList();
        var stashSection = Section("STASHES", stashes.Count);
        foreach (var s in stashes)
        {
            stashSection.Children.Add(new SidebarNode(SidebarNodeKind.Stash, s.Message)
            {
                Stash = s,
                Subtitle = s.Branch,
                ToolTip = $"{s.RefName}: {s.Message}\n{(s.Branch is null ? "" : $"On {s.Branch}, ")}{s.When.LocalDateTime:g}",
            });
        }
        Nodes.Add(stashSection);

        var worktrees = _worktrees.Where(w => Match(w.Name) || Match(w.Branch ?? "")).ToList();
        var leftovers = _leftovers.Where(p => Match(Path.GetFileName(p))).ToList();
        var wtSection = new SidebarNode(SidebarNodeKind.Section, "WORKTREES")
        {
            Count = worktrees.Count,
            IsWorktreesSection = true,
            IsExpanded = !_collapsed.Contains("WORKTREES") || filter.Length > 0,
        };
        foreach (var w in worktrees)
        {
            var isCurrent = WorktreeService.SamePath(w.Path, _currentWorktree ?? "");
            var branchRef = w.Branch is null ? null : _refs.FirstOrDefault(r => r.Kind == RefKind.LocalBranch && r.Name == w.Branch);
            var state = w.IsPrunable ? " (missing)" : w.IsLocked ? " (locked)" : "";
            wtSection.Children.Add(new SidebarNode(SidebarNodeKind.Worktree, w.IsMain ? $"{w.Name} (main)" : w.Name)
            {
                Worktree = w,
                KindIcon = BranchIcons.ForBranch(w.Branch),
                Target = branchRef is not null ? BranchTarget.From(branchRef, w) : w.HeadSha is not null ? new BranchTarget(RefKind.DetachedHead, "HEAD", w.HeadSha, Worktree: w) : null,
                IsCurrent = isCurrent,
                IsDimmed = w.IsPrunable || w.IsLocked,
                Subtitle = w.Branch ?? (w.IsDetached ? "detached" : null),
                ToolTip = $"{w.Path}\n{(w.Branch is null ? "detached HEAD" : w.Branch)}{state}",
            });
        }
        wtSection.ContainsCurrent = wtSection.Children.Any(c => c.IsCurrent);
        foreach (var path in leftovers)
        {
            wtSection.Children.Add(new SidebarNode(SidebarNodeKind.Worktree, Path.GetFileName(path))
            {
                LeftoverPath = path,
                IsDimmed = true,
                Subtitle = "leftover",
                ToolTip = $"{path}\nNot a registered worktree any more (removal was interrupted). Right-click to delete it.",
            });
        }
        Nodes.Add(wtSection);

        if (!_showPullRequests) return;
        var prs = _pullRequests.Where(p => Match($"#{p.Number} {p.Title}") || Match(p.Author.Login) || Match(p.HeadRef)).ToList();
        // Within each group, the most recently updated first (the order GitHub lists them in).
        var prSection = new SidebarNode(SidebarNodeKind.Section, "PULL REQUESTS")
        {
            Count = prs.Count,
            IsPullRequestsSection = true,
            IsExpanded = !_collapsed.Contains("PULL REQUESTS") || filter.Length > 0,
        };
        if (_pullRequestsMessage is { } message)
            prSection.Children.Add(new SidebarNode(SidebarNodeKind.Message, message) { IsDimmed = true, ToolTip = message });
        // Tickets: from the title, else from the branch name when it uses a project seen in the titles ("E4").
        var titled = prs.ToDictionary(p => p.Number, p => PullRequestTriage.Ticket(p.Title));
        var projects = titled.Values.Where(t => t.Key is not null).Select(t => PullRequestTriage.Project(t.Key!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        const int shownReviewers = 2;
        foreach (var group in prs.GroupBy(PullRequestTriage.Group).OrderBy(g => g.Key))
        {
            var key = "PULL REQUESTS/" + group.Key;
            var heading = new SidebarNode(SidebarNodeKind.Folder, group.Key switch
            {
                PrGroup.NeedsYourReview => "Needs your review",
                PrGroup.Yours => "Yours",
                PrGroup.Others => "Others",
                _ => "Drafts",
            })
            {
                IsPrGroup = true,
                Count = group.Count(),
                FolderKey = key,
                IsExpanded = !_collapsed.Contains(key) || filter.Length > 0,
            };
            foreach (var p in group)
            {
                var (ticket, title) = titled[p.Number];
                ticket ??= PullRequestTriage.TicketFromBranch(p.HeadRef, projects);
                var author = Person(p.Author.Login, p.Author.AvatarUrl, $"Opened by {p.Author.Login}");
                var reviewers = p.Reviewers.Select(r => Person(r.Name, r.AvatarUrl, PrPerson.Describe(r), r.State)).ToList();
                heading.Children.Add(new SidebarNode(SidebarNodeKind.PullRequest, title)
                {
                    PullRequest = p,
                    Author = author,
                    Reviewers = reviewers.Take(shownReviewers).ToList(),
                    MoreReviewers = reviewers.Count > shownReviewers ? $"+{reviewers.Count - shownReviewers}" : null,
                    IsDimmed = p.IsDraft,
                    NumberText = $"#{p.Number}",
                    Ticket = ticket,
                    RailBrush = PrRailColors.For(PullRequestTriage.Rail(p)),
                    Card = new PrCardViewModel(p, author, reviewers, ticket),
                    ToolTip = $"#{p.Number} {p.Title}",
                });
            }
            prSection.Children.Add(heading);
        }
        Nodes.Add(prSection);
    }

    private SidebarNode Section(string name, int count) => new(SidebarNodeKind.Section, name)
    {
        Count = count,
        IsExpanded = !_collapsed.Contains(name) || Filter.Trim().Length > 0,
    };

    /// <summary>
    /// Builds the branch rows of a section, nested as the branch rules say: by the slashes in their names, or under
    /// a rule's group ("bug/x" under "bugs"). A branch named like a group folder becomes that folder, so the bugs
    /// branch holds the bug/* branches.
    /// </summary>
    private sealed class BranchTree(SidebarViewModel owner, SidebarNode section, string? remote)
    {
        private readonly List<(string Name, IReadOnlyList<string> Folders, string Label, SidebarNode Node)> _items = [];

        public void Add(string name, Func<string, SidebarNode> factory)
        {
            var (folders, label) = BranchRuleSet.Current.Place(name);
            if (remote is { Length: > 0 }) folders = [remote, .. folders];
            _items.Add((name, folders, label, factory(label)));
        }

        public void Build()
        {
            var byPath = new Dictionary<string, SidebarNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _items) byPath.TryAdd(string.Join('/', [.. item.Folders, item.Label]), item.Node);
            var containers = new Dictionary<string, SidebarNode>(StringComparer.OrdinalIgnoreCase);
            var placed = new HashSet<SidebarNode>();
            var filtering = owner.Filter.Trim().Length > 0;
            // Rules apply below the remote's folder, so that's the level whose folders get icons.
            var ruleDepth = remote is { Length: > 0 } ? 1 : 0;

            SidebarNode Container(IReadOnlyList<string> folders, int depth, string name)
            {
                var path = string.Join('/', folders.Take(depth + 1));
                if (containers.TryGetValue(path, out var existing)) return existing;
                var parent = depth == 0 ? section : Container(folders, depth - 1, name);
                var key = section.Label + "/" + path;
                if (byPath.TryGetValue(path, out var branch))
                {
                    // A branch with the group's name: its row is the folder.
                    branch.FolderKey = key;
                    branch.IsExpanded = !owner._collapsed.Contains(key) || filtering;
                    InsertFolder(parent, branch);
                    placed.Add(branch);
                    containers[path] = branch;
                    return branch;
                }
                var rulePath = string.Join('/', folders.Skip(ruleDepth).Take(depth + 1 - ruleDepth));
                var folder = new SidebarNode(SidebarNodeKind.Folder, folders[depth])
                {
                    KindIcon = depth == ruleDepth ? BranchIcons.ForFolder(rulePath) : null,
                    IsExpanded = !owner._collapsed.Contains(key) || filtering,
                    ToolTip = key,
                    FolderKey = key,
                    BranchPrefix = ruleDepth == 0 ? RealPrefix(name, folders.Count, depth) : null,
                };
                InsertFolder(parent, folder);
                containers[path] = folder;
                return folder;
            }

            foreach (var (name, folders, _, node) in _items)
            {
                var parent = folders.Count == 0 ? section : Container(folders, folders.Count - 1, name);
                if (placed.Add(node)) parent.Children.Add(node);
            }

            // Show where the current branch is while its folders are collapsed.
            foreach (var (_, folders, _, _) in _items.Where(i => i.Node.IsCurrent))
            {
                section.ContainsCurrent = true;
                for (var depth = 0; depth < folders.Count; depth++)
                    if (containers.TryGetValue(string.Join('/', folders.Take(depth + 1)), out var c)) c.ContainsCurrent = true;
            }
        }

        /// <summary>Folders (and branches holding others) sort before plain branches, like a file tree.</summary>
        private static void InsertFolder(SidebarNode parent, SidebarNode folder)
        {
            var at = parent.Children.TakeWhile(c => c.IsFolder || c.FolderKey is not null).Count();
            parent.Children.Insert(at, folder);
        }

        /// <summary>
        /// The real name prefix of the branches in a folder ("bug/" for the bugs group of bug/x), for creating a
        /// branch in it; null when the folder doesn't stand for one.
        /// </summary>
        private static string? RealPrefix(string name, int folderCount, int depth)
        {
            var parts = name.Split('/');
            var below = folderCount - depth;
            return parts.Length > below ? string.Join('/', parts[..^below]) + "/" : null;
        }
    }

    private static string KeyOf(SidebarNode n) => n.IsSection ? n.Label : n.FolderKey ?? n.ToolTip ?? n.Label;

    private static IEnumerable<SidebarNode> Flatten(IEnumerable<SidebarNode> nodes) =>
        nodes.SelectMany(n => Flatten(n.Children).Prepend(n));
}
