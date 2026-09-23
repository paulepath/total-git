using Avalonia.Controls;
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
    }

    public event Action<string>? CopyRequested;
}
