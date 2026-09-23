using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TotalGit.Core.Git;

namespace TotalGit.App.Services;

/// <summary>Icons that stand in for the feature/, bug/ and hot-fix/ branch prefixes.</summary>
public static class BranchIcons
{
    private static readonly Lazy<Bitmap> Feature = new(() => Load("branch-feature.png"));
    private static readonly Lazy<Bitmap> Bug = new(() => Load("branch-bug.png"));
    private static readonly Lazy<Bitmap> HotFix = new(() => Load("branch-hot-fix.png"));

    /// <summary>The icon for a kind, or null for ordinary branches (which keep the plain glyph).</summary>
    public static Bitmap? For(BranchKind kind) => kind switch
    {
        BranchKind.Feature => Feature.Value,
        BranchKind.Bug => Bug.Value,
        BranchKind.HotFix => HotFix.Value,
        _ => null,
    };

    public static Bitmap? ForBranch(string? name) => For(BranchCategory.Classify(name).Kind);

    private static Bitmap Load(string file) =>
        new(AssetLoader.Open(new Uri($"avares://TotalGit/Assets/{file}")));
}
