using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TotalGit.App.Services;
using TotalGit.App.ViewModels;
using TotalGit.App.Views;
using TotalGit.Core.Avatars;

namespace TotalGit.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var avatars = new AvatarService(Path.Combine(AppSettings.DataDirectory, "avatars"));
            var settings = AppSettings.Load();
            var updates = new UpdateService();
            var shell = new ShellViewModel(avatars, settings, updates);
            desktop.MainWindow = new MainWindow { DataContext = shell };
            desktop.Exit += (_, _) =>
            {
                foreach (var tab in shell.Tabs) tab.Dispose();
                avatars.Dispose();
                updates.ApplyOnExit(); // a downloaded update the user didn't restart for
            };

            // Reopen last session's tabs; a folder passed on the command line opens (or selects) a tab too.
            shell.RestoreTabs(desktop.Args is [var first, ..] ? first : null);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
