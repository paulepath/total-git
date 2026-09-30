using Avalonia.Data.Converters;
using Avalonia.Media;

namespace TotalGit.App.ViewModels;

public static class Converters
{
    /// <summary>Full-row tint for the current branch / worktree in the sidebar.</summary>
    public static readonly IValueConverter CurrentRowBrush =
        new FuncValueConverter<bool, IBrush>(b => b ? new SolidColorBrush(Color.Parse("#4CC38A"), 0.14) : Brushes.Transparent);

    /// <summary>Opacity for rows that can't be picked.</summary>
    public static readonly IValueConverter DimWhenFalse = new FuncValueConverter<bool, double>(b => b ? 1 : 0.55);

    /// <summary>A pull request's icon: blue when your review is requested.</summary>
    public static readonly IValueConverter ReviewRequestedBrush =
        new FuncValueConverter<bool, IBrush>(b => new SolidColorBrush(Color.Parse(b ? "#5AA9F2" : "#8A9099")));

    /// <summary>A chevron pointing down when expanded, right when collapsed.</summary>
    public static readonly IValueConverter ExpandedRotation =
        new FuncValueConverter<bool, ITransform>(b => new RotateTransform(b ? 90 : 0));

    public static readonly IValueConverter ErrorToBackground =
        new FuncValueConverter<bool, IBrush>(b => new SolidColorBrush(Color.Parse(b ? "#5A2327" : "#1F3A5A")));
}
