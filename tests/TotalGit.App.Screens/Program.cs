using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using TotalGit.App;
using TotalGit.App.Services;
using TotalGit.App.ViewModels;
using TotalGit.App.Views;
using TotalGit.Core.Avatars;
using TotalGit.Core.Hosting;
using TotalGit.Core.Hosting.GitHub;

namespace TotalGit.App.Screens;

/// <summary>
/// Saves screenshots of Total Git's windows, drawn off-screen, in one theme:
/// <c>dotnet run -- --repo &lt;clone&gt; --out &lt;folder&gt; --theme Dark|Light [--pr &lt;number&gt;] [--commit &lt;rev&gt;]
/// [--conflict &lt;file with conflict markers&gt;] [--only &lt;scene prefix&gt;]</c>.
/// The repository should be a throwaway clone: opening it fetches. Settings live in a scratch data folder, so the
/// real Total Git's are never read or written.
/// </summary>
internal static class Program
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();

    public static int Main(string[] args)
    {
        var options = Options.Parse(args);
        Directory.CreateDirectory(options.Out);
        // Before anything reads AppSettings: its data folder comes from here.
        var data = Path.Combine(Path.GetTempPath(), "totalgit-screens", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("TOTALGIT_DATA_DIR", data);

        BuildAvaloniaApp().SetupWithoutStarting();
        var exit = 1;
        using var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Screens.RunAsync(options);
                exit = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
            }
            finally
            {
                done.Cancel();
            }
        });
        // The dispatcher runs here, as in the app, so git, GitHub calls and timers carry on while we wait for them.
        Dispatcher.UIThread.MainLoop(done.Token);
        return exit;
    }
}

internal sealed record Options(string Repo, string Out, ThemeVariant Theme, int? Pr, string? Only, int Width, int Height, string? Commit, string? Conflict)
{
    public static Options Parse(string[] args)
    {
        string Arg(string name, string? fallback = null)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback ?? throw new ArgumentException($"{name} is required");
        }
        var theme = Arg("--theme", "Dark").Equals("light", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Light : ThemeVariant.Dark;
        var pr = Arg("--pr", "") is { Length: > 0 } n ? int.Parse(n) : (int?)null;
        var only = Arg("--only", "") is { Length: > 0 } o ? o : null;
        return new Options(Path.GetFullPath(Arg("--repo")), Path.GetFullPath(Arg("--out")), theme, pr, only,
            int.Parse(Arg("--width", "1600")), int.Parse(Arg("--height", "1000")),
            Arg("--commit", "") is { Length: > 0 } c ? c : null,
            Arg("--conflict", "") is { Length: > 0 } f ? Path.GetFullPath(f) : null);
    }
}

