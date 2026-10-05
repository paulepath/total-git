using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

/// <summary>
/// The pull request review window's content: files with review boxes on the left, the overview or one file in the
/// middle, conversation / checks / the pending review on the right. Single keys move through the review.
/// </summary>
public partial class PullRequestReviewView : UserControl
{
    private PullRequestReviewViewModel? _vm;

    public PullRequestReviewView()
    {
        InitializeComponent();
        DiffView.AttachScrollBar(DiffScrollBar);
        DiffView.AttachHorizontalScrollBar(DiffHScrollBar);
        DiffView.AddCommentRequested += (_, lineIndex) => _vm?.StartComment(lineIndex);
        OverviewTab.PointerPressed += (_, e) =>
        {
            _vm?.ShowOverviewCommand.Execute(null);
            e.Handled = true;
        };
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as PullRequestReviewViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        // A local review has no conversation or checks: no right-hand pane.
        if (_vm is { IsLocal: true })
        {
            Panes.ColumnDefinitions[3].Width = new GridLength(0);
            Panes.ColumnDefinitions[4].Width = new GridLength(0);
        }
        ShowDiffThreads();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PullRequestReviewViewModel.DiffThreads) or nameof(PullRequestReviewViewModel.CanCommentOnDiff)) ShowDiffThreads();
        // Opening a file: keyboard focus to the diff, so J/K and the arrow keys act on it.
        if (e.PropertyName == nameof(PullRequestReviewViewModel.SelectedFile) && _vm?.SelectedFile is not null) DiffView.Focus();
    }

    /// <summary>Review threads and comment boxes go between the diff's lines as real controls (new ones each time).</summary>
    private void ShowDiffThreads()
    {
        if (_vm is null) return;
        DiffView.CanAddComments = _vm.CanCommentOnDiff;
        DiffView.Annotations = _vm.DiffThreads
            .Select(t => new DiffAnnotation(t.LineIndex, new ReviewThreadView { DataContext = t.ViewModel }))
            .ToList();
    }

    /// <summary>
    /// N / P next or previous file, J / K next or previous change, R reviewed and next, C comment, O overview,
    /// F5 reload. Not while typing.
    /// </summary>
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
        if (e.Source is TextBox || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
        var handled = true;
        switch (e.Key)
        {
            case Key.N: _vm.NextFileCommand.Execute(null); break;
            case Key.P: _vm.PreviousFileCommand.Execute(null); break;
            case Key.J when _vm.HasOpenFile: DiffView.ScrollToNextChange(1); break;
            case Key.K when _vm.HasOpenFile: DiffView.ScrollToNextChange(-1); break;
            case Key.R when _vm.HasOpenFile: _vm.MarkReviewedAndNextCommand.Execute(null); break;
            case Key.C when _vm.HasOpenFile && DiffView.LineNearCentre() is { } line: _vm.StartComment(line); break;
            case Key.O: _vm.ShowOverviewCommand.Execute(null); break;
            case Key.F5: _vm.LoadCommand.Execute(null); break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }
}
