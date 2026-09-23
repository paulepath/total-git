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
            var vm = new MainWindowViewModel(avatars, settings);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.Exit += (_, _) => avatars.Dispose();

            var startPath = desktop.Args is [var first, ..] ? first : settings.LastRepository;
            if (!string.IsNullOrEmpty(startPath) && Directory.Exists(startPath))
                _ = vm.LoadAsync(startPath);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
