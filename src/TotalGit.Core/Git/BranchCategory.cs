namespace TotalGit.Core.Git;

public enum BranchKind
{
    Other,
    Feature,
    Bug,
    HotFix,
    Main,
}

/// <summary>
/// Recognises the feature/, bug/ and hot-fix/ naming convention, so the UI can show an icon
/// in place of the prefix ("feature/e4-1-x" → ✦ "e4-1-x"). main/master get an icon too, keeping their name.
/// </summary>
public static class BranchCategory
{
    private static readonly (string Prefix, BranchKind Kind)[] Prefixes =
    [
        ("feature/", BranchKind.Feature),
        ("features/", BranchKind.Feature),
        ("bug/", BranchKind.Bug),
        ("bugs/", BranchKind.Bug),
        ("bugfix/", BranchKind.Bug),
        ("hot-fix/", BranchKind.HotFix),
        ("hotfix/", BranchKind.HotFix),
    ];

    /// <summary>The branch's kind and its name without the prefix (unchanged for Other).</summary>
    public static (BranchKind Kind, string ShortName) Classify(string? name)
    {
        if (string.IsNullOrEmpty(name)) return (BranchKind.Other, name ?? "");
        if (name is "main" or "master") return (BranchKind.Main, name);
        foreach (var (prefix, kind) in Prefixes)
        {
            if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (kind, name[prefix.Length..]);
        }
        return (BranchKind.Other, name);
    }

    /// <summary>The kind of a folder name in the sidebar tree ("feature", "bugs", "hot-fix").</summary>
    public static BranchKind ForFolder(string folder)
    {
        var kind = Classify(folder + "/x").Kind;
        return kind == BranchKind.Main ? BranchKind.Other : kind;
    }
}
