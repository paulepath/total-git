using Avalonia.Controls;
using Avalonia.Threading;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

public partial class MergeToolView : UserControl
{
    private MergeToolViewModel? _vm;

    public MergeToolView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.ScrollRequested -= OnScrollRequested;
        _vm = DataContext as MergeToolViewModel;
        if (_vm is null) return;
        _vm.ScrollRequested += OnScrollRequested;
        OnScrollRequested(0, 0);
    }

    // Brings the current conflict into view with a few lines of context above it.
    private void OnScrollRequested(int oursLine, int theirsLine) => Dispatcher.UIThread.Post(() =>
    {
        Reveal(OursPane, oursLine);
        Reveal(TheirsPane, theirsLine);
    }, DispatcherPriority.Background);

    private static void Reveal(ListBox pane, int line)
    {
        var count = pane.ItemCount;
        if (count == 0) return;
        pane.ScrollIntoView(Math.Min(count - 1, line + 12));
        pane.ScrollIntoView(Math.Max(0, line - 3));
    }
}
