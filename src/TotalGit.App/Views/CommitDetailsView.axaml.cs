using Avalonia.Controls;
using Avalonia.Threading;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

public partial class CommitDetailsView : UserControl
{
    private readonly DispatcherTimer _copiedTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public CommitDetailsView()
    {
        InitializeComponent();
        CopyShaButton.Click += (_, _) =>
        {
            if (DataContext is not CommitDetailsViewModel vm) return;
            CopyRequested?.Invoke(vm.Sha);
            // Show that it worked: the chip reads "Copied ✓" for a moment.
            ShowCopied(true);
            _copiedTimer.Stop();
            _copiedTimer.Start();
        };
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer.Stop();
            ShowCopied(false);
        };
        Files.FileContextRequested += (file, row) => FileContextRequested?.Invoke(file, row);
    }

    private void ShowCopied(bool copied)
    {
        ShaText.IsVisible = !copied;
        CopiedText.IsVisible = copied;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _copiedTimer.Stop();
        ShowCopied(false);
    }

    public event Action<string>? CopyRequested;

    /// <summary>Right-click on a file in the commit's file list.</summary>
    public event Action<FileChangeItem, Control>? FileContextRequested;
}
