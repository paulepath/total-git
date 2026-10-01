using Avalonia;
using Avalonia.Controls;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

/// <summary>The hover card of a workflow run row; its jobs load the first time it's shown.</summary>
public partial class RunHoverCard : UserControl
{
    public RunHoverCard() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as RunCardViewModel)?.EnsureJobs();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (IsAttachedToVisualTree()) (DataContext as RunCardViewModel)?.EnsureJobs();
    }

    private bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;
}
