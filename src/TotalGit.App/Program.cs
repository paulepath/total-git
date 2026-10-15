using Avalonia;
using Velopack;

namespace TotalGit.App;

internal static class Program
{
    /// <returns>0 normally; with <c>--merge</c>, how the merge ended (see <see cref="TotalGit.Core.Git.MergeExit"/>).</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles Velopack's install/uninstall/update hooks and may exit the process.
        VelopackApp.Build().Run();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
