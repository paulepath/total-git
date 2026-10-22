using Microsoft.Toolkit.Uwp.Notifications;
using TotalGit.Core.Diagnostics;
using TotalGit.Core.Hosting;

namespace TotalGit.App.Services;

/// <summary>A click on one of Total Git's notifications.</summary>
/// <param name="Url">The pull request it was about; null for a summary of several.</param>
/// <param name="OnGitHub">The "Open on GitHub" button rather than the notification itself.</param>
public sealed record ToastClick(string? Url, bool OnGitHub);

/// <summary>
/// Windows notifications ("toasts": bottom right, then kept in the Notification Center), with Windows' notification
/// sound. Installed copies show as "Total Git" with its icon (Velopack gives the app its identity). A click on one
/// after Total Git has closed starts it again with <c>-ToastActivated</c>.
/// </summary>
public static class Toasts
{
    private const string UrlKey = "url";
    private const string ActionKey = "action";

    private static readonly Lock Gate = new();
    private static readonly ManualResetEventSlim Clicked = new();
    private static Action<ToastClick>? _handler;
    private static ToastClick? _pending;

    /// <summary>Listens for clicks. Call early: a click that started Total Git arrives straight away.</summary>
    public static void Start()
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += e => Deliver(Parse(e.Argument));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Windows notifications aren't available: {ex.Message}");
        }
    }

    /// <summary>Total Git was started by a click on one of its notifications.</summary>
    public static bool WasStartedByClick
    {
        get
        {
            try { return ToastNotificationManagerCompat.WasCurrentProcessToastActivated(); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>The click that started Total Git, waiting a moment for it (it's still delivered to <see cref="OnClicked"/>).</summary>
    public static ToastClick? WaitForStartingClick(TimeSpan timeout)
    {
        Clicked.Wait(timeout);
        lock (Gate) return _pending;
    }

    /// <summary>Clicks go to <paramref name="handler"/> (on a background thread), starting with one already waiting.</summary>
    public static void OnClicked(Action<ToastClick> handler)
    {
        ToastClick? pending;
        lock (Gate)
        {
            _handler = handler;
            pending = _pending;
            _pending = null;
        }
        if (pending is not null) handler(pending);
    }

    private static void Deliver(ToastClick click)
    {
        Action<ToastClick>? handler;
        lock (Gate)
        {
            handler = _handler;
            if (handler is null) _pending = click;
        }
        Clicked.Set();
        handler?.Invoke(click);
    }

    private static ToastClick Parse(string argument)
    {
        var args = ToastArguments.Parse(argument);
        return new(args.TryGetValue(UrlKey, out var url) ? url : null, args.TryGetValue(ActionKey, out var action) && action == "github");
    }

    public static void Show(PrNotification notification, bool sound)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument(ActionKey, "open")
                .AddText(notification.Title)
                .AddText(notification.Text)
                .AddAttributionText(notification.Attribution)
                .AddAudio(sound ? new ToastAudio { Src = new Uri("ms-winsoundevent:Notification.Default") } : new ToastAudio { Silent = true });
            if (notification.Url is { } url)
            {
                builder.AddArgument(UrlKey, url);
                builder.AddButton(new ToastButton().SetContent("Open on GitHub").AddArgument(ActionKey, "github").AddArgument(UrlKey, url));
            }
            builder.Show();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Couldn't show a Windows notification: {ex.Message}");
        }
    }

    /// <summary>Takes Total Git's notifications out of Windows (when it's uninstalled).</summary>
    public static void Uninstall()
    {
        try { ToastNotificationManagerCompat.Uninstall(); }
        catch (Exception) { }
    }
}
