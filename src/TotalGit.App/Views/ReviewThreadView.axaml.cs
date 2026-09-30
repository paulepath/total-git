using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

/// <summary>A card between diff lines: a review thread, a comment being written, or a pending review comment.</summary>
public partial class ReviewThreadView : UserControl
{
    public ReviewThreadView()
    {
        InitializeComponent();
        // A new comment box takes the keyboard straight away.
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is CommentComposerViewModel)
                Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus(), DispatcherPriority.Loaded);
        };
    }
}
