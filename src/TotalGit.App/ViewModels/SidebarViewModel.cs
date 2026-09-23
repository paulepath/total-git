using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TotalGit.Core.Git;
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
    public partial bool IsExpanded { get; set; }

    public BranchTarget? Target { get; init; }
    public WorktreeInfo? Worktree { get; init; }
    public bool IsCurrent { get; init; }
    public int Count { get; init; }
    public string? Ahead { get; init; }
    public string? Behind { get; init; }
    public string? ToolTip { get; init; }
    public string? Subtitle { get; init; }
    public bool HasSubtitle => Subtitle is not null;
    public bool HasWorktree { get; init; }
    public bool IsDimmed { get; init; }

    public bool IsSection => Kind == SidebarNodeKind.Section;
    public bool IsFolder => Kind == SidebarNodeKind.Folder;
    public bool IsBranch => Kind is SidebarNodeKind.LocalBranch or SidebarNodeKind.RemoteBranch;
    public bool IsTag => Kind == SidebarNodeKind.Tag;
    public bool IsWorktree => Kind == SidebarNodeKind.Worktree;
    public bool IsWorktreesSection { get; init; }
    public bool ShowCount => IsSection;
    public bool HasAhead => Ahead is not null;
    public bool HasBehind => Behind is not null;
}

/// <summary>GitKraken-style left panel: LOCAL, REMOTE, TAGS and WORKTREES with a filter.</summary>
public partial class SidebarViewModel : ObservableObject
{
    private IReadOnlyList<RefInfo> _refs = [];
    private IReadOnlyList<WorktreeInfo> _worktrees = [];
    private string? _currentWorktree;
    private readonly HashSet<string> _collapsed = ["TAGS"];

    public ObservableCollection<SidebarNode> Nodes { get; } = [];

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial SidebarNode? SelectedNode { get; set; }

    partial void OnFilterChanged(string value) => Rebuild();

    public void Update(IReadOnlyList<RefInfo> refs, IReadOnlyList<WorktreeInfo> worktrees, string currentWorktree)
    {
        _refs = refs;
        _worktrees = worktrees;
        _currentWorktree = currentWorktree;
        Rebuild();
    }

    public void Clear()
    {
        _refs = [];
        _worktrees = [];
        Nodes.Clear();
    }

    private void Rebuild()
    {
        // Remember which folders/sections the user collapsed or expanded.
        foreach (var n in Flatten(Nodes).Where(n => n.IsSection || n.IsFolder))
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
        foreach (var r in locals)
        {
            worktreeByBranch.TryGetValue(r.Name, out var wt);
            var otherWorktree = wt is not null && !WorktreeService.SamePath(wt.Path, _currentWorktree ?? "") ? wt : null;
            AddPath(local, r.Name, leaf => new SidebarNode(SidebarNodeKind.LocalBranch, leaf)
            {
                Target = BranchTarget.From(r, wt),
                IsCurrent = r.IsCurrent,
                Ahead = r.Ahead > 0 ? $"{r.Ahead}↑" : null,
                Behind = r.Behind > 0 ? $"{r.Behind}↓" : null,
                HasWorktree = otherWorktree is not null,
                ToolTip = otherWorktree is not null ? $"{r.Name}\nChecked out in worktree {otherWorktree.Path}" : r.Name,
            });
        }
        Nodes.Add(local);

        var remotes = _refs.Where(r => r.Kind == RefKind.RemoteBranch && Match(r.Name)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var remote = Section("REMOTE", remotes.Count);
        foreach (var r in remotes)
        {
            AddPath(remote, r.Name, leaf => new SidebarNode(SidebarNodeKind.RemoteBranch, leaf)
            {
                Target = BranchTarget.From(r, null),
                ToolTip = r.Name,
            });
        }
        Nodes.Add(remote);

        var tags = _refs.Where(r => r.Kind == RefKind.Tag && Match(r.Name)).OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var tagSection = Section("TAGS", tags.Count);
        foreach (var r in tags)
            tagSection.Children.Add(new SidebarNode(SidebarNodeKind.Tag, r.Name) { Target = BranchTarget.From(r, null), ToolTip = r.Name });
        Nodes.Add(tagSection);

        var worktrees = _worktrees.Where(w => Match(w.Name) || Match(w.Branch ?? "")).ToList();
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
                Target = branchRef is not null ? BranchTarget.From(branchRef, w) : w.HeadSha is not null ? new BranchTarget(RefKind.DetachedHead, "HEAD", w.HeadSha, Worktree: w) : null,
                IsCurrent = isCurrent,
                IsDimmed = w.IsPrunable || w.IsLocked,
                Subtitle = w.Branch ?? (w.IsDetached ? "detached" : null),
                ToolTip = $"{w.Path}\n{(w.Branch is null ? "detached HEAD" : w.Branch)}{state}",
            });
        }
        Nodes.Add(wtSection);
    }

    private SidebarNode Section(string name, int count) => new(SidebarNodeKind.Section, name)
    {
        Count = count,
        IsExpanded = !_collapsed.Contains(name) || Filter.Trim().Length > 0,
    };

    /// <summary>Adds a leaf under nested folders for each '/' in the name (feature/x → feature › x).</summary>
    private void AddPath(SidebarNode section, string name, Func<string, SidebarNode> leafFactory)
    {
        var parts = name.Split('/');
        var parent = section;
        var key = section.Label;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            key += "/" + parts[i];
            var folder = parent.Children.FirstOrDefault(c => c.IsFolder && c.Label == parts[i]);
            if (folder is null)
            {
                folder = new SidebarNode(SidebarNodeKind.Folder, parts[i])
                {
                    IsExpanded = !_collapsed.Contains(key) || Filter.Trim().Length > 0,
                    ToolTip = key,
                };
                // Folders sort before leaves, like a file tree.
                var insertAt = parent.Children.TakeWhile(c => c.IsFolder).Count();
                parent.Children.Insert(insertAt, folder);
            }
            parent = folder;
        }
        parent.Children.Add(leafFactory(parts[^1]));
    }

    private static string KeyOf(SidebarNode n) => n.IsSection ? n.Label : n.ToolTip ?? n.Label;

    private static IEnumerable<SidebarNode> Flatten(IEnumerable<SidebarNode> nodes) =>
        nodes.SelectMany(n => Flatten(n.Children).Prepend(n));
}
