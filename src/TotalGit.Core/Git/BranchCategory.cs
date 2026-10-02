namespace TotalGit.Core.Git;

public enum BranchKind
{
    Other,
    Feature,
    Features,
    Bug,
    Bugs,
    HotFix,
    Main,
}

/// <summary>
/// The kind of a branch under the active <see cref="BranchRuleSet"/>, for the built-in icons ("feature/e4-1-x" →
/// ✦ "e4-1-x"; main/master keep their name). Branches matched by a rule with another icon are <see cref="BranchKind.Other"/>.
/// </summary>
public static class BranchCategory
{
    /// <summary>The branch's kind and the name to show (without the prefix when its rule hides it).</summary>
    public static (BranchKind Kind, string ShortName) Classify(string? name)
    {
        var match = BranchRuleSet.Current.Match(name);
        return (KindOf(match.Rule), match.ShortName);
    }

    /// <summary>Whether a branch is a main line (its rule says so): the graph draws its line thicker.</summary>
    public static bool IsMainLine(string? name) => BranchRuleSet.Current.Match(name).Rule?.IsMainLine == true;

    /// <summary>The kind of a folder name in the sidebar tree ("feature", "bugs", "hot-fix").</summary>
    public static BranchKind ForFolder(string folder) => KindOf(BranchRuleSet.Current.ForFolder(folder));

    private static BranchKind KindOf(BranchRule? rule) => rule?.Icon switch
    {
        "builtin:feature" => BranchKind.Feature,
        "builtin:features" => BranchKind.Features,
        "builtin:bug" => BranchKind.Bug,
        "builtin:bugs" => BranchKind.Bugs,
        "builtin:hot-fix" => BranchKind.HotFix,
        "builtin:main" => BranchKind.Main,
        _ => BranchKind.Other,
    };
}
