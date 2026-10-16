using System.Collections.Generic;
using System.Windows;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using TotalGit.VisualStudio.Git;

namespace TotalGit.VisualStudio.UI;

/// <summary>A row of the Worktrees window (and of the Switch Worktree list).</summary>
public sealed class WorktreeItem
{
    internal WorktreeItem(WorktreeEntry entry) => Entry = entry;

    internal WorktreeEntry Entry { get; }

    public string Name => Entry.Name;

    /// <summary>"feature/x  ↑2 ↓1  ●3  · main worktree".</summary>
    public string Summary
    {
        get
        {
            if (!Entry.Exists) return "folder missing";
            var parts = new List<string> { Entry.BranchText, Porcelain.AheadBehind(Entry.Status) };
            if (Entry.Status is { Changes: > 0 } s) parts.Add("●" + s.Changes);
            if (Entry.Worktree.IsMain) parts.Add("· main worktree");
            return string.Join("  ", parts.FindAll(p => p.Length > 0));
        }
    }

    public FontWeight Weight => Entry.Current ? FontWeights.Bold : FontWeights.Normal;

    public ImageMoniker Icon =>
        !Entry.Exists ? KnownMonikers.StatusWarning
        : Entry.Status is { Conflicts.Count: > 0 } ? KnownMonikers.Conflict
        : Entry.Current ? KnownMonikers.FolderOpened
        : KnownMonikers.FolderClosed;

    public string ToolTip
    {
        get
        {
            var lines = new List<string> { Entry.Name + (Entry.Current ? " (open here)" : ""), Entry.Folder };
            if (Entry.BranchText.Length > 0) lines.Add(Entry.BranchText + (Entry.Status?.Upstream is { } up ? " → " + up : ""));
            if (Entry.Status is { } s)
            {
                if (s.Ahead > 0 || s.Behind > 0) lines.Add($"{s.Ahead} to push, {s.Behind} to pull");
                lines.Add(s.Changes == 0 ? "No uncommitted changes" : $"{s.Changes} uncommitted change{(s.Changes == 1 ? "" : "s")}"
                    + (s.Conflicts.Count > 0 ? $", {s.Conflicts.Count} conflicted" : ""));
            }
            else if (!Entry.Exists)
            {
                lines.Add("The folder is gone; Total Git (or git worktree prune) can tidy it up.");
            }
            if (Entry.Worktree.Locked) lines.Add("Locked");
            if (!Entry.Current && Entry.Exists) lines.Add("Double-click to switch to it.");
            return string.Join("\n", lines);
        }
    }
}
