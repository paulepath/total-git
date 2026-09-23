using Avalonia.Data.Converters;
using Avalonia.Media;

namespace TotalGit.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter BoolToSemiBold =
        new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

    public static readonly IValueConverter DimmedBrush =
        new FuncValueConverter<bool, IBrush>(b => b ? new SolidColorBrush(Color.Parse("#6B727C")) : new SolidColorBrush(Color.Parse("#D5D8DD")));

    public static readonly IValueConverter ErrorToBackground =
        new FuncValueConverter<bool, IBrush>(b => new SolidColorBrush(Color.Parse(b ? "#5A2327" : "#1F3A5A")));
}
