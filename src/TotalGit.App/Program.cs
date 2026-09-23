using Avalonia;
using Velopack;

namespace TotalGit.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack's install/uninstall/update hooks and may exit the process.
        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
