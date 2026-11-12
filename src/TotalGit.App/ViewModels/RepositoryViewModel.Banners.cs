using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TotalGit.App.ViewModels;

// Messages never move the window's content. Plain confirmations light up the status bar along the bottom for a few
// seconds and then stay there as the latest message. Errors, and messages with something to do, are cards over the
// bottom right of the graph that wait until they're dealt with. Every message also goes into the history.
public partial class RepositoryViewModel
{
    private static readonly TimeSpan StatusLitTime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan StatusLitAfterPointer = TimeSpan.FromSeconds(2);
    private const int MaxNotices = 3;
    private const int MaxHistory = 50;

    private DispatcherTimer? _statusTimer;
    private Banner? _lastBanner;

    /// <summary>
    /// Shows a message: in the status bar, or as a card when it's an error or has actions. Reading it gives the last
    /// message shown; setting null closes the cards.
    /// </summary>
    public Banner? Banner
    {
        get => _lastBanner;
        set
        {
            if (value is null) Notices.Clear();
            else Post(value);
        }
    }

    /// <summary>The cards showing now, oldest first.</summary>
    public ObservableCollection<Notice> Notices { get; } = [];

    /// <summary>Every message shown in this tab, newest first.</summary>
    public ObservableCollection<NoticeEntry> NoticeHistory { get; } = [];

    public bool HasNoticeHistory => NoticeHistory.Count > 0;

    /// <summary>The latest confirmation, kept in the status bar.</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    /// <summary>The status message is new: it's highlighted for a few seconds.</summary>
    [ObservableProperty]
    public partial bool StatusLit { get; private set; }

    private void Post(Banner banner)
    {
        _lastBanner = banner;
        NoticeHistory.Insert(0, new NoticeEntry(banner.Message, banner.IsError, DateTime.Now));
        while (NoticeHistory.Count > MaxHistory) NoticeHistory.RemoveAt(NoticeHistory.Count - 1);
        OnPropertyChanged(nameof(HasNoticeHistory));

        if (!banner.IsError && !banner.HasActions)
        {
            ShowStatus(banner.Message);
            return;
        }

        // A message that repeats or adds to one already showing takes its place.
        foreach (var old in Notices.Where(n => banner.Message.Contains(n.Message, StringComparison.Ordinal)).ToList())
            Notices.Remove(old);
        IReadOnlyList<MenuAction> actions = banner.CanReport
            ? [.. banner.Actions, new MenuAction("Report", ReportErrorCommand, banner.Message), new MenuAction("Copy details", CopyErrorDetailsCommand, banner.Message)]
            : banner.Actions;
        Notices.Add(new Notice(banner.Message, banner.IsError, actions, n => Notices.Remove(n)));
        while (Notices.Count > MaxNotices) Notices.RemoveAt(0);
    }

    private void ShowStatus(string message)
    {
        StatusMessage = message;
        StatusLit = true;
        if (_statusTimer is null)
        {
            _statusTimer = new DispatcherTimer();
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                StatusLit = false;
            };
        }
        _statusTimer.Stop();
        _statusTimer.Interval = StatusLitTime;
        _statusTimer.Start();
    }

    /// <summary>The pointer is over the status message: keep it highlighted while it's being read.</summary>
    public void PauseStatusTimer(bool paused)
    {
        if (_statusTimer is null || !StatusLit) return;
        _statusTimer.Stop();
        if (paused) return;
        _statusTimer.Interval = StatusLitAfterPointer;
        _statusTimer.Start();
    }

    /// <summary>Forgets the cards, the status message and the history (another repository opened in this tab).</summary>
    private void ClearMessages()
    {
        Notices.Clear();
        NoticeHistory.Clear();
        OnPropertyChanged(nameof(HasNoticeHistory));
        _statusTimer?.Stop();
        StatusMessage = null;
        StatusLit = false;
        _lastBanner = null;
    }
}

/// <summary>A card over the graph: an error, or a message with something to do. Its buttons close it.</summary>
public sealed class Notice
{
    public Notice(string message, bool isError, IReadOnlyList<MenuAction> actions, Action<Notice> close)
    {
        Message = message;
        IsError = isError;
        CloseCommand = new RelayCommand(() => close(this));
        Actions = [.. actions.Select(a => a.Command is not { } command ? a : a with
        {
            Command = new RelayCommand<object?>(p =>
            {
                close(this);
                command.Execute(p);
            }, command.CanExecute),
        })];
    }

    public string Message { get; }
    public bool IsError { get; }
    public IReadOnlyList<MenuAction> Actions { get; }
    public bool HasActions => Actions.Count > 0;
    public IRelayCommand CloseCommand { get; }
}

/// <summary>A message in the history.</summary>
public sealed record NoticeEntry(string Message, bool IsError, DateTime At)
{
    public string Time => At.ToString("HH:mm");
}
