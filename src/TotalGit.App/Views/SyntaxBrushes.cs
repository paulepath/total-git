using Avalonia.Media;
using TotalGit.Core.Git;

namespace TotalGit.App.Views;

/// <summary>Applies syntax colours to a line's text, as drawn by the diff and merge views (tabs as four spaces).</summary>
internal static class SyntaxBrushes
{
    private static readonly Dictionary<uint, IBrush> Brushes = [];

    /// <summary>Colours <paramref name="text"/>, the drawn form of <paramref name="raw"/>, with its spans.</summary>
    public static void Apply(FormattedText text, string raw, IReadOnlyList<SyntaxSpan>? spans)
    {
        if (spans is null || spans.Count == 0) return;
        var hasTabs = raw.Contains('\t');
        foreach (var s in spans)
        {
            var start = hasTabs ? DisplayIndex(raw, s.Start) : s.Start;
            var end = hasTabs ? DisplayIndex(raw, s.Start + s.Length) : s.Start + s.Length;
            if (end <= start) continue;
            if (s.Argb != 0) text.SetForegroundBrush(Brush(s.Argb), start, end - start);
            if (s.Bold) text.SetFontWeight(FontWeight.Bold, start, end - start);
            if (s.Italic) text.SetFontStyle(FontStyle.Italic, start, end - start);
        }
    }

    private static IBrush Brush(uint argb)
    {
        if (!Brushes.TryGetValue(argb, out var brush))
            Brushes[argb] = brush = new SolidColorBrush(Color.FromUInt32(argb)).ToImmutable();
        return brush;
    }

    /// <summary>Where a raw index lands once tabs are drawn as four spaces.</summary>
    public static int DisplayIndex(string text, int index)
    {
        var tabs = 0;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\t') tabs++;
        return index + tabs * 3;
    }
}
