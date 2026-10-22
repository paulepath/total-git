using Avalonia;
using Velopack;

namespace TotalGit.App;

internal static class Program
{
    /// <returns>0 normally (also when handed to the Total Git already running); with <c>--merge</c>, how the merge ended (see <see cref="TotalGit.Core.Git.MergeExit"/>).</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles Velopack's install/uninstall/update hooks and may exit the process.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Services.Toasts.Uninstall())
            .Run();

        // Clicks on Total Git's notifications; one may be what started it (with -ToastActivated).
        Services.Toasts.Start();
        var click = Services.Toasts.WasStartedByClick ? Services.Toasts.WaitForStartingClick(TimeSpan.FromSeconds(3)) : null;
        if (click is { OnGitHub: true, Url: { } url })
        {
            Services.UrlLauncher.Open(url);
            return 0;
        }

        // Already running: it opens the folder or pull request (or just comes to the front) instead of a second copy starting.
        if (!TotalGit.Core.Git.MergeCommandLine.IsMerge(args)
            && Services.SingleInstance.TryForward(click is null ? args.FirstOrDefault() : null, click?.Url))
            return 0;
        // Logs errors for error reports, and saves a crash for the next start to offer reporting.
        Services.ErrorReporter.Start();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
