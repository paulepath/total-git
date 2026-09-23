using Avalonia.Data.Converters;
using Avalonia.Media;

namespace TotalGit.App.ViewModels;

public static class Converters
{
    /// <summary>Full-row tint for the current branch / worktree in the sidebar.</summary>
    public static readonly IValueConverter CurrentRowBrush =
        new FuncValueConverter<bool, IBrush>(b => b ? new SolidColorBrush(Color.Parse("#4CC38A"), 0.14) : Brushes.Transparent);

    public static readonly IValueConverter ErrorToBackground =
        new FuncValueConverter<bool, IBrush>(b => new SolidColorBrush(Color.Parse(b ? "#5A2327" : "#1F3A5A")));
}
