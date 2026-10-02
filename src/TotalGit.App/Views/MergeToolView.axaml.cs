using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using TotalGit.App.ViewModels;
using TotalGit.Core.Git;

namespace TotalGit.App.Views;

public partial class MergeToolView : UserControl
{
    // Close enough to measure how far the longest line reaches (the panes use a monospace font).
    private const double CharWidth = 7.6;

    private readonly MergeScroll _scroll = new();
    private MergeToolViewModel? _vm;
    private bool _syncing;
    private TextBox? _editor;
    private int _editSegment = -1;

    public MergeToolView()
    {
        InitializeComponent();
        foreach (var pane in Panes) pane.AttachScroll(_scroll);
        Overview.AttachScroll(_scroll);
        _scroll.Changed += SyncScrollBars;
        VScroll.SmallChange = MergeScroll.LineHeight;
        VScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_syncing) _scroll.SetOffset(VScroll.Value);
        };
        SourceHScroll.SmallChange = CharWidth * 4;
        SourceHScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_syncing) _scroll.SetHOffset(SourceHScroll.Value);
        };
        ResultHScroll.SmallChange = CharWidth * 4;
        ResultHScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_syncing) _scroll.SetResultHOffset(ResultHScroll.Value);
        };
        ResultPane.SegmentEditRequested += BeginEdit;
        // Tunnel: the shortcuts work whichever pane has the focus (but not while typing).
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
    }

    private IEnumerable<MergePaneView> Panes => [BasePane, OursPane, TheirsPane, ResultPane];

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.RevealConflict -= OnRevealConflict;
            _vm.PropertyChanged -= OnViewModelChanged;
        }
        CancelEdit();
        _vm = DataContext as MergeToolViewModel;
        foreach (var pane in Panes) pane.Model = _vm;
        Overview.Model = _vm;
        if (_vm is null) return;
        _vm.RevealConflict += OnRevealConflict;
        _vm.PropertyChanged += OnViewModelChanged;
        _scroll.SetHOffset(0);
        _scroll.SetResultHOffset(0);
        OnLayoutChanged();
        ApplyShowBase();
        // Once the panes have their sizes, show the first conflict.
        Dispatcher.UIThread.Post(() => OnRevealConflict(_vm?.CurrentIndex ?? 0), DispatcherPriority.Background);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MergeToolViewModel.Layout):
                OnLayoutChanged();
                break;
            case nameof(MergeToolViewModel.ShowBase):
                ApplyShowBase();
                break;
            case nameof(MergeToolViewModel.IsTextMode):
                CancelEdit();
                break;
        }
    }

    private void OnLayoutChanged()
    {
        var layout = _vm?.Layout;
        _scroll.SetRowCount(layout?.Rows.Count ?? 0);
        PositionEditor();
    }

    private void OnRevealConflict(int conflict)
    {
        if (_vm?.Layout is not { } layout || conflict < 0 || conflict >= layout.ConflictRows.Count) return;
        var (first, count) = layout.ConflictRows[conflict];
        _scroll.Reveal(first, count);
    }

    /// <summary>The base pane takes a third of the width when shown, none when hidden.</summary>
    private void ApplyShowBase()
    {
        var show = _vm?.ShowBase == true;
        var columns = SourcePanes.ColumnDefinitions;
        columns[0].Width = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        columns[1].Width = new GridLength(show ? 4 : 0);
        BaseColumn.IsVisible = show;
        BaseSplitter.IsVisible = show;
    }

    private void SyncScrollBars()
    {
        _syncing = true;
        VScroll.Maximum = _scroll.MaxOffset;
        VScroll.ViewportSize = _scroll.Viewport;
        VScroll.LargeChange = Math.Max(MergeScroll.LineHeight, _scroll.Viewport - MergeScroll.LineHeight);
        VScroll.Value = _scroll.Offset;

        // The source panes share one sideways scroll: as far as their longest line needs in the narrowest of them.
        var layout = _vm?.Layout;
        var sourceLongest = 0;
        var resultLongest = 0;
        if (layout is not null)
        {
            if (_vm!.ShowBase) foreach (var l in layout.BaseLines) sourceLongest = Math.Max(sourceLongest, Expanded(l));
            foreach (var l in layout.OursLines) sourceLongest = Math.Max(sourceLongest, Expanded(l));
            foreach (var l in layout.TheirsLines) sourceLongest = Math.Max(sourceLongest, Expanded(l));
            foreach (var l in layout.ResultLines) resultLongest = Math.Max(resultLongest, Expanded(l.Text));
        }
        var narrowest = new[] { BasePane, OursPane, TheirsPane }.Where(p => p.IsEffectivelyVisible && p.Bounds.Width > 0)
            .Select(p => p.Bounds.Width).DefaultIfEmpty(0).Min();
        SyncSideways(SourceHScroll, sourceLongest, narrowest, _scroll.HOffset, max => _scroll.MaxHOffset = max);
        SyncSideways(ResultHScroll, resultLongest, ResultPane.Bounds.Width, _scroll.ResultHOffset, max => _scroll.MaxResultHOffset = max);
        _syncing = false;
        PositionEditor();
    }

    /// <summary>Sizes a sideways scroll bar to a pane's width and its longest line; hidden when everything fits.</summary>
    private void SyncSideways(ScrollBar bar, int longest, double width, double value, Action<double> setMax)
    {
        // The gutter (pick margin and line numbers) plus a little room after the end of the line.
        var max = width <= 0 ? 0 : Math.Max(0, longest * CharWidth + 90 - width);
        setMax(max);
        bar.Maximum = max;
        bar.ViewportSize = Math.Max(1, width);
        bar.LargeChange = Math.Max(CharWidth, width / 2);
        bar.Value = Math.Min(value, max);
        bar.IsVisible = max > 0 && _vm?.IsPaneMode == true;
    }

    /// <summary>A line's length on screen, with tabs drawn as four spaces.</summary>
    private static int Expanded(string line) => line.Length + line.Count(c => c == '\t') * 3;

    // ------------------------------------------------------------------ keys

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (_vm is not { IsText: true, IsPaneMode: true } vm || e.Source is TextBox) return;
        var ctrl = e.KeyModifiers == KeyModifiers.Control;
        var ctrlShift = e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.D1 or Key.NumPad1 when ctrl:
                vm.ToggleSideCommand.Execute(MergeSide.Base);
                break;
            case Key.D2 or Key.NumPad2 when ctrl:
                vm.ToggleSideCommand.Execute(MergeSide.Ours);
                break;
            case Key.D3 or Key.NumPad3 when ctrl:
                vm.ToggleSideCommand.Execute(MergeSide.Theirs);
                break;
            case Key.Down when ctrlShift:
                vm.NextUnresolvedCommand.Execute(null);
                break;
            case Key.Down when ctrl:
                if (vm.NextCommand.CanExecute(null)) vm.NextCommand.Execute(null);
                break;
            case Key.Up when ctrl:
                if (vm.PreviousCommand.CanExecute(null)) vm.PreviousCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ typing over a block of the result

    private void BeginEdit(int segment)
    {
        if (_vm is null) return;
        CancelEdit();
        _editSegment = segment;
        var editor = new TextBox
        {
            Text = string.Join("\n", _vm.SegmentText(segment)),
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 12.5,
            Background = new SolidColorBrush(Color.Parse("#16191D")),
            BorderBrush = new SolidColorBrush(Color.Parse("#A371F7")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(62, 0, 4, 0),
            LineHeight = MergeScroll.LineHeight,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        ToolTip.SetTip(editor, "Ctrl+Enter to keep, Esc to cancel");
        // Tunnel: the text box would otherwise take Ctrl+Enter as a new line.
        editor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CancelEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
            {
                CommitEdit();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        editor.LostFocus += (_, _) =>
        {
            if (ReferenceEquals(_editor, editor)) CommitEdit();
        };
        _editor = editor;
        EditLayer.Children.Add(editor);
        PositionEditor();
        Dispatcher.UIThread.Post(() =>
        {
            editor.Focus();
            editor.CaretIndex = editor.Text?.Length ?? 0;
        });
    }

    private void CommitEdit()
    {
        if (_editor is not { } editor || _vm is null) return;
        var segment = _editSegment;
        var text = (editor.Text ?? "").Replace("\r\n", "\n");
        RemoveEditor();
        IReadOnlyList<string> lines = text.Length == 0 ? [] : text.Split('\n');
        if (!lines.SequenceEqual(_vm.SegmentText(segment))) _vm.EditSegment(segment, lines);
    }

    private void CancelEdit() => RemoveEditor();

    private void RemoveEditor()
    {
        if (_editor is null) return;
        var editor = _editor;
        _editor = null;
        _editSegment = -1;
        EditLayer.Children.Remove(editor);
        ResultPane.Focus();
    }

    /// <summary>Keeps the editor over its block as the panes scroll (at least three rows tall).</summary>
    private void PositionEditor()
    {
        if (_editor is null || _vm?.Layout is not { } layout || _editSegment < 0 || _editSegment >= layout.SegmentRows.Count) return;
        var (first, count) = layout.SegmentRows[_editSegment];
        var top = first * MergeScroll.LineHeight - _scroll.Offset;
        Canvas.SetLeft(_editor, 0);
        Canvas.SetTop(_editor, top);
        _editor.Width = Math.Max(100, ResultHost.Bounds.Width);
        _editor.Height = Math.Max(3, count + 1) * MergeScroll.LineHeight + 4;
    }
}
