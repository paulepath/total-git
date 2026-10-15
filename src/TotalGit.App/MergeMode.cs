using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using TotalGit.App.Services;
using TotalGit.App.ViewModels;
using TotalGit.App.Views;
using TotalGit.Core.Git;

namespace TotalGit.App;

/// <summary>
/// <c>TotalGit --merge &lt;file&gt;</c>: one window with the merge tool for one conflicted file, for git mergetool or
/// another app to run and wait for. It reads and writes no settings, restores no tabs, and never changes the index:
/// staging the result is the caller's job. The exit code (<see cref="MergeExit"/>) says how it ended, with a one-line
/// reason on stderr.
/// </summary>
internal static class MergeMode
{
    public static void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // The checks run before any window opens, and some end without one.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Once the main loop runs: shutting down before it starts throws.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RunAsync(desktop));
    }

    private static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (MergeCommandLine.Parse(desktop.Args ?? []) is not { } line)
        {
            Finish(desktop, MergeExit.Usage, "Usage: TotalGit --merge <file>");
            return;
        }

        MergeFileCheck check;
        try
        {
            check = await MergeFileCheck.RunAsync(Path.GetFullPath(line.File));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            Finish(desktop, MergeExit.Usage, ex.Message);
            return;
        }
        if (check.Target is not { } target)
        {
            Finish(desktop, check.Exit, check.Reason);
            return;
        }

        var window = new MergeWindow(target.Path);
        var exit = MergeExit.Cancelled;
        var reason = $"Closed without saving {target.Path}.";
        MergeToolViewModel tool;
        try
        {
            tool = await MergeToolViewModel.LoadAsync(target.Worktree, target.Path, AppSettings.Detached(),
                // The file is written; staging it is the caller's job.
                markResolved: _ =>
                {
                    (exit, reason) = (MergeExit.Saved, $"Saved {target.Path}.");
                    window.Close();
                    return Task.FromResult(true);
                },
                takeWholeFile: async (path, ours) =>
                {
                    try
                    {
                        await GitActions.CheckoutSideAsync(target.Worktree, path, ours);
                    }
                    catch (GitCommandException ex)
                    {
                        await window.ShowMessageAsync("Couldn't take that side", ex.Message);
                        return;
                    }
                    (exit, reason) = (MergeExit.Saved, $"Took {(ours ? "ours" : "theirs")} for {target.Path}.");
                    window.Close();
                },
                confirm: window.ConfirmAsync,
                close: window.Close,
                standaloneMessage: window.ShowMessageAsync);
        }
        catch (IOException ex)
        {
            Finish(desktop, MergeExit.Usage, ex.Message);
            return;
        }

        window.DataContext = tool;
        window.Closed += (_, _) => Finish(desktop, exit, reason);
        desktop.MainWindow = window;
        window.Show();
        window.Activate();
    }

    private static void Finish(IClassicDesktopStyleApplicationLifetime desktop, MergeExit exit, string reason)
    {
        // A GUI app has no console, but a caller that redirects stderr (git, the Workbench) gets this line.
        try
        {
            Console.Error.WriteLine(reason);
            Console.Error.Flush();
        }
        catch (IOException) { }
        desktop.Shutdown((int)exit);
    }
}
