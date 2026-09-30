using Avalonia.Controls;

namespace TotalGit.App.Views;

/// <summary>The commits picked with Shift+click, with the action to rebase them onto another branch.</summary>
public partial class CommitRangeView : UserControl
{
    public CommitRangeView() => InitializeComponent();
}
