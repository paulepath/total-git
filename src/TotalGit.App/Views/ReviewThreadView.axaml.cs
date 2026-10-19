using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace TotalGit.App.Views;

/// <summary>A card between diff lines: a review thread, a comment being written, or a pending review comment.</summary>
public partial class ReviewThreadView : UserControl
{
    static ReviewThreadView()
    {
        // Picking a reaction closes the picker (its buttons live in a flyout, outside this view's tree). Only once the
        // click is done: closing first takes the button out of the tree, and its command loses its reaction.
        Button.ClickEvent.AddClassHandler<Button>((button, _) =>
        {
            if (button.Classes.Contains("pick") && button.FindAncestorOfType<PopupRoot>()?.Parent is Popup popup)
                Dispatcher.UIThread.Post(() => popup.IsOpen = false);
        }, RoutingStrategies.Bubble);
    }

    /// <summary>
    /// The card last clicked in. A thread shows in the diff and the conversation at once, sharing its reply box: the
    /// cursor goes to the box in the card being used.
    /// </summary>
    public static ReviewThreadView? Active { get; private set; }

    public ReviewThreadView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, (_, _) => Active = this, RoutingStrategies.Tunnel, handledEventsToo: true);
        DetachedFromVisualTree += (_, _) =>
        {
            if (Active == this) Active = null;
        };
    }
}
