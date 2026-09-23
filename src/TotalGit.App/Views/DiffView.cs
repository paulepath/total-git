using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using TotalGit.Core.Git;

namespace TotalGit.App.Views;

/// <summary>Unified diff viewer with line numbers; draws only the visible lines.</summary>
public sealed class DiffView : Control
{
    public static readonly StyledProperty<FileDiff?> DiffProperty =
        AvaloniaProperty.Register<DiffView, FileDiff?>(nameof(Diff));

    private const double LineHeight = 19;
    private const double GutterWidth = 46;
    private const double FontSize = 12.5;

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#1C1F24"));
    private static readonly IBrush GutterBrush = new SolidColorBrush(Color.Parse("#20242A"));
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.Parse("#2FBF71"), 0.16);
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Color.Parse("#E5392F"), 0.18);
    private static readonly IBrush AddedGutterBrush = new SolidColorBrush(Color.Parse("#2FBF71"), 0.28);
    private static readonly IBrush RemovedGutterBrush = new SolidColorBrush(Color.Parse("#E5392F"), 0.3);
    private static readonly IBrush HunkBrush = new SolidColorBrush(Color.Parse("#2D7BF4"), 0.14);
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#DCDFE4"));
    private static readonly IBrush HunkTextBrush = new SolidColorBrush(Color.Parse("#7FA8E8"));
    private static readonly IBrush NumberBrush = new SolidColorBrush(Color.Parse("#6B727C"));
    private static readonly IBrush AddedSignBrush = new SolidColorBrush(Color.Parse("#4CC38A"));
    private static readonly IBrush RemovedSignBrush = new SolidColorBrush(Color.Parse("#F26B6B"));
    private static readonly IBrush NoticeBrush = new SolidColorBrush(Color.Parse("#8A9099"));

    private readonly Typeface _mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");
    private double _offset;
    private double _hOffset;
    private double _charWidth = 7.5;
    private ScrollBar? _scrollBar;
    private bool _syncing;

    static DiffView()
    {
        AffectsRender<DiffView>(DiffProperty);
        ClipToBoundsProperty.OverrideDefaultValue<DiffView>(true);
        FocusableProperty.OverrideDefaultValue<DiffView>(true);
    }

    public FileDiff? Diff
    {
        get => GetValue(DiffProperty);
        set => SetValue(DiffProperty, value);
    }

    private int LineCount => (Diff?.Lines.Count ?? 0) + (Diff?.Truncated == true ? 1 : 0);
    private double MaxOffset => Math.Max(0, LineCount * LineHeight - Bounds.Height);

    public void AttachScrollBar(ScrollBar scrollBar)
    {
        _scrollBar = scrollBar;
        scrollBar.SmallChange = LineHeight;
        scrollBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_syncing) SetOffset(scrollBar.Value);
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DiffProperty)
        {
            var oldPath = change.GetOldValue<FileDiff?>()?.Path;
            var newPath = change.GetNewValue<FileDiff?>()?.Path;
            if (oldPath != newPath) _hOffset = 0;
            SetOffset(oldPath == newPath ? _offset : 0);
        }
        else if (change.Property == BoundsProperty)
        {
            SetOffset(_offset);
        }
    }

    private void SetOffset(double value)
    {
        _offset = Math.Clamp(value, 0, MaxOffset);
        if (_scrollBar is not null)
        {
            _syncing = true;
            _scrollBar.Maximum = MaxOffset;
            _scrollBar.ViewportSize = Bounds.Height;
            _scrollBar.LargeChange = Math.Max(LineHeight, Bounds.Height - LineHeight);
            _scrollBar.Value = _offset;
            _scrollBar.IsVisible = MaxOffset > 0;
            _syncing = false;
        }
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.X != 0)
        {
            var delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
            _hOffset = Math.Max(0, _hOffset - delta * _charWidth * 6);
            InvalidateVisual();
        }
        else
        {
            SetOffset(_offset - e.Delta.Y * LineHeight * 3);
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var page = Math.Max(LineHeight, Bounds.Height - LineHeight);
        double? target = e.Key switch
        {
            Key.Down => _offset + LineHeight,
            Key.Up => _offset - LineHeight,
            Key.PageDown => _offset + page,
            Key.PageUp => _offset - page,
            Key.Home => 0,
            Key.End => MaxOffset,
            _ => null,
        };
        if (target is { } t)
        {
            SetOffset(t);
            e.Handled = true;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    public override void Render(DrawingContext ctx)
    {
        var width = Bounds.Width;
        ctx.FillRectangle(Background, new Rect(Bounds.Size));
        var diff = Diff;
        if (diff is null) return;

        if (diff.IsBinary || diff.Lines.Count == 0)
        {
            var notice = Text(diff.IsBinary ? "Binary file — no text diff." : "No changes to show.", NoticeBrush);
            ctx.DrawText(notice, new Point(16, 16));
            return;
        }

        _charWidth = Text("M", TextBrush).WidthIncludingTrailingWhitespace;
        var textLeft = GutterWidth * 2 + 20;
        ctx.FillRectangle(GutterBrush, new Rect(0, 0, GutterWidth * 2, Bounds.Height));

        var first = Math.Max(0, (int)(_offset / LineHeight));
        var last = Math.Min(LineCount - 1, (int)((_offset + Bounds.Height) / LineHeight));
        for (var i = first; i <= last; i++)
        {
            var y = i * LineHeight - _offset;
            if (i >= diff.Lines.Count)
            {
                ctx.DrawText(Text($"Diff truncated after {diff.Lines.Count:N0} lines.", NoticeBrush), new Point(textLeft, y + 2));
                continue;
            }

            var line = diff.Lines[i];
            var (bg, gutterBg, sign, signBrush) = line.Kind switch
            {
                DiffLineKind.Added => (AddedBrush, AddedGutterBrush, "+", AddedSignBrush),
                DiffLineKind.Removed => (RemovedBrush, RemovedGutterBrush, "-", RemovedSignBrush),
                DiffLineKind.Hunk => (HunkBrush, HunkBrush, "", TextBrush),
                _ => ((IBrush?)null, (IBrush?)null, "", TextBrush),
            };
            if (bg is not null) ctx.FillRectangle(bg, new Rect(GutterWidth * 2, y, width - GutterWidth * 2, LineHeight));
            if (gutterBg is not null) ctx.FillRectangle(gutterBg, new Rect(0, y, GutterWidth * 2, LineHeight));

            if (line.OldLine is { } o) DrawNumber(ctx, o, 0, y);
            if (line.NewLine is { } n) DrawNumber(ctx, n, GutterWidth, y);

            using (ctx.PushClip(new Rect(GutterWidth * 2, y, width - GutterWidth * 2, LineHeight)))
            {
                if (sign.Length > 0) ctx.DrawText(Text(sign, signBrush), new Point(GutterWidth * 2 + 6, y + 2));
                var brush = line.Kind is DiffLineKind.Hunk or DiffLineKind.NoNewline ? HunkTextBrush : TextBrush;
                ctx.DrawText(Text(line.Text.Replace("\t", "    "), brush), new Point(textLeft - _hOffset, y + 2));
            }
        }
    }

    private void DrawNumber(DrawingContext ctx, int number, double left, double y)
    {
        var ft = Text(number.ToString(CultureInfo.InvariantCulture), NumberBrush);
        ctx.DrawText(ft, new Point(left + GutterWidth - 6 - ft.Width, y + 2));
    }

    private FormattedText Text(string text, IBrush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _mono, FontSize, brush);
}
