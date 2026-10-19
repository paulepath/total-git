using Avalonia.Data.Converters;
using Avalonia.Media;
using TotalGit.App.Services;

namespace TotalGit.App.ViewModels;

public static class Converters
{
    /// <summary>Full-row tint for the current branch / worktree in the sidebar.</summary>
    public static readonly IValueConverter CurrentRowBrush =
        new FuncValueConverter<bool, IBrush>(b => b ? Palette.ChangedRow : Brushes.Transparent);

    /// <summary>Opacity for rows that can't be picked.</summary>
    public static readonly IValueConverter DimWhenFalse = new FuncValueConverter<bool, double>(b => b ? 1 : 0.55);

    /// <summary>A pull request's icon: blue when your review is requested.</summary>
    public static readonly IValueConverter ReviewRequestedBrush =
        new FuncValueConverter<bool, IBrush>(b => b ? Palette.Accent : Palette.TextMuted);

    /// <summary>A chevron pointing down when expanded, right when collapsed.</summary>
    public static readonly IValueConverter ExpandedRotation =
        new FuncValueConverter<bool, ITransform>(b => new RotateTransform(b ? 90 : 0));

    public static readonly IValueConverter ErrorToBackground =
        new FuncValueConverter<bool, IBrush>(b => b ? Palette.ErrorBanner : Palette.InfoBanner);
}
