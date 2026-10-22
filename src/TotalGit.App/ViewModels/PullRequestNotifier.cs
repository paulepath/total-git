using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Diagnostics;
using TotalGit.Core.Hosting;

namespace TotalGit.App.ViewModels;

/// <summary>
/// Watches the user's pull requests on GitHub (every repository, every signed-in account) while Total Git runs, and
/// tells them with a Windows notification when their review is asked for, they're assigned, or one of theirs is
/// approved, gets changes requested or comments, or fails its checks. What's been seen is remembered between runs,
/// so news from while Total Git was closed comes on the next start.
/// </summary>
public sealed partial class PullRequestNotifier : ObservableObject
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(10);
    // Accounts can be added or signed out of: the inboxes are made again now and then.
    private static readonly TimeSpan InboxesFor = TimeSpan.FromMinutes(30);

    private readonly IPullRequestInboxSource _source;
    private readonly AppSettings _settings;
    private readonly string _statePath = Path.Combine(AppSettings.DataDirectory, "notifications.json");
    private readonly DispatcherTimer _timer = new() { Interval = FirstCheck };
    private IReadOnlyList<IPullRequestInbox>? _inboxes;
    private DateTimeOffset _inboxesAt;
    private DateTimeOffset _lastCheck;
    private DateTimeOffset _waitUntil;
    private bool _checking;

    public PullRequestNotifier(IPullRequestInboxSource source, AppSettings settings)
    {
        _source = source;
        _settings = settings;
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = Interval;
            _ = CheckAsync();
        };
    }

    /// <summary>Raised (on the UI thread) after notifications were shown, e.g. to flash the taskbar button.</summary>
    public event Action? Notified;

    private NotificationSettings Options => _settings.Notifications;

    /// <summary>Notifications came that the user hasn't looked at Total Git since (the bell shows a dot).</summary>
    [ObservableProperty]
    public partial bool HasUnseen { get; set; }

    /// <summary>When it last asked GitHub, or why it couldn't, for the bell's menu.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "Not checked yet";

    public void Start() => _timer.Start();

    /// <summary>The window came to the front: the news has been seen, and it's a good moment to check again.</summary>
    public void OnWindowActivated()
    {
        HasUnseen = false;
        if (DateTimeOffset.Now - _lastCheck > TimeSpan.FromMinutes(1)) _ = CheckAsync();
    }

    [RelayCommand]
    private Task CheckNowAsync() => CheckAsync(force: true);

    [RelayCommand]
    private void SendTest() => Toasts.Show(new PrNotification("Test notification from Total Git",
        "Pull request news will look like this.", "Total Git", null, []), Options.Sound);

    private async Task CheckAsync(bool force = false)
    {
        if (_checking || !force && DateTimeOffset.Now < _waitUntil) return;
        _checking = true;
        try
        {
            if (_inboxes is null || DateTimeOffset.Now - _inboxesAt > InboxesFor)
            {
                _inboxes = await Task.Run(() => _source.CreateInboxesAsync());
                _inboxesAt = DateTimeOffset.Now;
            }
            var seen = LoadSeen();
            var news = new List<InboxEvent>();
            var problems = new List<string>();
            foreach (var inbox in _inboxes)
            {
                try
                {
                    var now = await Task.Run(() => inbox.GetAsync());
                    news.AddRange(PullRequestNotifications.Diff(seen.GetValueOrDefault(now.Viewer), now, DateTimeOffset.UtcNow, out var nowSeen));
                    seen[now.Viewer] = nowSeen;
                }
                catch (HostException ex)
                {
                    problems.Add(ex.Message);
                    DiagnosticLog.Write($"Pull request notifications: {ex.Message}");
                    if (ex.Kind == HostErrorKind.RateLimited) _waitUntil = DateTimeOffset.Now.AddMinutes(15);
                    if (ex.Kind == HostErrorKind.NotAuthenticated) _inboxes = null; // signed out: ask gh again next time
                }
            }
            SaveSeen(seen);
            _lastCheck = DateTimeOffset.Now;
            Status = problems.Count == 0 ? $"Checked GitHub at {_lastCheck:HH:mm}" : $"Couldn't check GitHub: {problems[0]}";
            Show(news.Where(e => Options.Wants(e.Kind)).ToList());
        }
        catch (Exception ex)
        {
            // A poller in the background: never an error on screen, but on record.
            DiagnosticLog.Write($"Pull request notifications failed: {ex}");
            Status = $"Couldn't check GitHub: {ex.Message}";
        }
        finally
        {
            _checking = false;
        }
    }

    private void Show(IReadOnlyList<InboxEvent> news)
    {
        var notifications = PullRequestNotifications.Group(news);
        if (notifications.Count == 0) return;
        foreach (var n in notifications) Toasts.Show(n, Options.Sound);
        DiagnosticLog.Write($"Notified: {string.Join("; ", notifications.Select(n => $"{n.Title} ({n.Text})"))}");
        HasUnseen = true;
        Notified?.Invoke();
    }

    /// <summary>The events already told (or there when first checked), with when each was last there, per account.</summary>
    private Dictionary<string, IReadOnlyDictionary<string, DateTimeOffset>> LoadSeen()
    {
        try
        {
            if (File.Exists(_statePath))
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, DateTimeOffset>>>(File.ReadAllText(_statePath))?
                    .ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, DateTimeOffset>)kv.Value, StringComparer.OrdinalIgnoreCase) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Couldn't read {_statePath}: {ex.Message}");
        }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveSeen(Dictionary<string, IReadOnlyDictionary<string, DateTimeOffset>> seen)
    {
        try { File.WriteAllText(_statePath, JsonSerializer.Serialize(seen)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ the bell's menu

    public bool IsEnabled
    {
        get => Options.Enabled;
        set => Set(o => o.Enabled = value);
    }

    public bool PlaySound
    {
        get => Options.Sound;
        set => Set(o => o.Sound = value);
    }

    public bool OnReviewRequested
    {
        get => Options.ReviewRequested;
        set => Set(o => o.ReviewRequested = value);
    }

    public bool OnAssigned
    {
        get => Options.Assigned;
        set => Set(o => o.Assigned = value);
    }

    public bool OnApproved
    {
        get => Options.Approved;
        set => Set(o => o.Approved = value);
    }

    public bool OnChangesRequested
    {
        get => Options.ChangesRequested;
        set => Set(o => o.ChangesRequested = value);
    }

    public bool OnComments
    {
        get => Options.Comments;
        set => Set(o => o.Comments = value);
    }

    public bool OnChecksFailed
    {
        get => Options.ChecksFailed;
        set => Set(o => o.ChecksFailed = value);
    }

    private void Set(Action<NotificationSettings> change, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        change(Options);
        _settings.Save();
        OnPropertyChanged(property);
    }
}
