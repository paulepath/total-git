using Avalonia.Media.Imaging;

namespace TotalGit.App.Services;

/// <summary>The icon shown in place of a branch's prefix (or beside a folder), from the user's branch rules.</summary>
public static class BranchIcons
{
    /// <summary>The icon for a sidebar folder ("feature", "bugs", "hot-fix"), or null.</summary>
    public static Bitmap? ForFolder(string folder) => IconLibrary.ForFolder(folder);

    /// <summary>The icon for a branch, or null for branches no rule gives one.</summary>
    public static Bitmap? ForBranch(string? name) => IconLibrary.ForBranch(name);
}
