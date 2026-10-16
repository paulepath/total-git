using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TotalGit.App.Services;
using TotalGit.App.ViewModels;
using TotalGit.App.Views;
using TotalGit.Core.Avatars;
using TotalGit.Core.Hosting;
using TotalGit.Core.Hosting.GitHub;

namespace TotalGit.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Dark-tile icon in every window's title bar; the taskbar keeps the transparent one.
            Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => TitleBarIcon.Apply(w));

            // TotalGit --merge <file>: just the merge window, for git mergetool or another app.
            if (TotalGit.Core.Git.MergeCommandLine.IsMerge(desktop.Args ?? []))
            {
                MergeMode.Start(desktop);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var avatars = new AvatarService(Path.Combine(AppSettings.DataDirectory, "avatars"));
            var settings = AppSettings.Load();
            settings.ApplyBranchRules();
            var updates = new UpdateService();
            var pullRequests = new GitHubProviderFactory(ChainedCredentialSource.Default());
            var shell = new ShellViewModel(avatars, settings, updates, pullRequests, workflows: pullRequests);
            var window = new MainWindow { DataContext = shell };
            desktop.MainWindow = window;

            // Later starts hand their folder over to this copy.
            var instance = SingleInstance.Listen(path => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (path is not null && Directory.Exists(path)) _ = shell.OpenPathAsync(path);
                window.BringToFront();
            }));
            desktop.Exit += (_, _) =>
            {
                instance.Dispose();
                foreach (var tab in shell.Tabs) tab.Dispose();
                avatars.Dispose();
                pullRequests.Dispose();
                updates.ApplyOnExit(); // a downloaded update the user didn't restart for
            };

            // Reopen last session's tabs; a folder passed on the command line opens (or selects) a tab too.
            shell.RestoreTabs(desktop.Args is [var first, ..] ? first : null);

            // Older Total Git extensions in VS Code and Visual Studio get this release's, a little after start-up (Visual
            // Studio's only while it's closed).
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(async _ =>
            {
                await VsCodeExtension.UpdateIfOlderAsync(settings.VsCodePath);
                await VisualStudioExtension.UpdateIfOlderAsync();
            }, TaskScheduler.Default);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
