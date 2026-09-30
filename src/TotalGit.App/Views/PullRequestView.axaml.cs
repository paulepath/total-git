using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

/// <summary>One pull request: header, then its files, checks and conversation.</summary>
public partial class PullRequestView : UserControl
{
    public PullRequestView()
    {
        InitializeComponent();
        Files.FileContextRequested += (file, row) => FileContextRequested?.Invoke(file, row);
        OpenUrlCommand = new RelayCommand<string?>(url =>
        {
            if (url is not null) UrlLauncher.Open(url);
        });
    }

    /// <summary>Opens a check's details page.</summary>
    public IRelayCommand<string?> OpenUrlCommand { get; }

    /// <summary>Right-click on a changed file.</summary>
    public event Action<FileChangeItem, Control>? FileContextRequested;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // A newly opened pull request starts on its files.
        Tabs.SelectedIndex = 0;
    }
}
