using CommunityToolkit.Mvvm.ComponentModel;
using TotalGit.App.Services;
using TotalGit.Core.Git;

namespace TotalGit.App.ViewModels;

/// <summary>One local tag the remote doesn't have.</summary>
public sealed partial class TagCleanupItem(TagSummary tag) : ObservableObject, ICleanupItem
{
    public string Name { get; } = tag.Name;

    /// <summary>The commit it tags, and that it's annotated when it is.</summary>
    public string Where { get; } = tag.Sha[..Math.Min(7, tag.Sha.Length)] + (tag.IsAnnotated ? "  annotated" : "");

    public string Subject { get; } = tag.Subject;
    public string When { get; } = tag.Created is { } when ? DateText.Relative(when) : "";

    /// <summary>A warning that the tag is all that keeps its commit, or null.</summary>
    public string? LooseText { get; } = tag.OnNoBranch ? "Its commit is on no branch: once no tag points at it, git will eventually discard it." : null;
    public bool IsLoose => LooseText is not null;

    public bool IsSelectable => true;

    // Not ticked to start with: a tag that was never pushed may be one that's about to be.
    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>The "Clean up tags" dialog: pick which local tags that aren't on the remote to delete.</summary>
public sealed class TagCleanupViewModel(IEnumerable<TagSummary> tags, string remote)
    : CleanupListViewModel<TagCleanupItem>(tags.Select(t => new TagCleanupItem(t)), "tag", "tags")
{
    public string Intro { get; } =
        $"These local tags aren't on {remote}: they were never pushed, or were deleted there. Deleting removes them from this PC only; "
        + $"nothing changes on {remote}. Leave unticked any you still mean to push.";
}
