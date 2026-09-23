using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TotalGit.Core.Git;

namespace TotalGit.App.Services;

/// <summary>Icons that stand in for the feature/, bug/ and hot-fix/ branch prefixes (and mark main/master).</summary>
public static class BranchIcons
{
    private static readonly Lazy<Bitmap> Feature = new(() => Load("branch-feature.png"));
    private static readonly Lazy<Bitmap> Bug = new(() => Load("branch-bug.png"));
    private static readonly Lazy<Bitmap> HotFix = new(() => Load("branch-hot-fix.png"));
    private static readonly Lazy<Bitmap> Main = new(() => Load("branch-main.png"));
    private static readonly Lazy<Bitmap> Features = new(() => Load("branch-features.png"));
    private static readonly Lazy<Bitmap> Bugs = new(() => Load("branch-bugs.png"));

    /// <summary>The icon for a kind, or null for ordinary branches (which keep the plain glyph).</summary>
    public static Bitmap? For(BranchKind kind) => kind switch
    {
        BranchKind.Feature => Feature.Value,
        BranchKind.Bug => Bug.Value,
        BranchKind.HotFix => HotFix.Value,
        BranchKind.Main => Main.Value,
        _ => null,
    };

    /// <summary>The icon for a sidebar category folder: the plural (group) versions for features and bugs.</summary>
    public static Bitmap? ForFolder(string folder) => BranchCategory.ForFolder(folder) switch
    {
        BranchKind.Feature => Features.Value,
        BranchKind.Bug => Bugs.Value,
        var kind => For(kind),
    };

    public static Bitmap? ForBranch(string? name) => For(BranchCategory.Classify(name).Kind);

    private static Bitmap Load(string file) =>
        new(AssetLoader.Open(new Uri($"avares://TotalGit/Assets/{file}")));
}
