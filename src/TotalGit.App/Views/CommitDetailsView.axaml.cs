using Avalonia.Controls;
using Avalonia.VisualTree;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

public partial class CommitDetailsView : UserControl
{
    public CommitDetailsView()
    {
        InitializeComponent();
        CopyShaButton.Click += (_, _) =>
        {
            if (DataContext is CommitDetailsViewModel vm) CopyRequested?.Invoke(vm.Sha);
        };
        FileList.ContextRequested += (_, e) =>
        {
            if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: FileChangeItem file } item)
            {
                FileContextRequested?.Invoke(file, item);
                e.Handled = true;
            }
        };
    }

    public event Action<string>? CopyRequested;

    /// <summary>Right-click on a file in the commit's file list.</summary>
    public event Action<FileChangeItem, Control>? FileContextRequested;
}
