using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TotalGit.VisualStudio.Git;

namespace TotalGit.VisualStudio;

/// <summary>How <c>TotalGit.exe --merge</c> ended (its exit code).</summary>
internal enum MergeExit
{
    Saved = 0,
    Cancelled = 1,
    Usage = 2,
    Unsupported = 3,
}

/// <summary>Starts Total Git and VS Code.</summary>
internal static class ExternalApps
{
    public const string ReleasesUrl = "https://github.com/paulepath/total-git/releases/latest";

    /// <summary>The Total Git executable: the configured path, else where its installer puts it.</summary>
    public static string? FindTotalGit(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        // current\TotalGit.exe, not the "Total Git.exe" launcher beside it: only the app itself passes back an exit code.
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TotalGitApp", "current", "TotalGit.exe");
        return File.Exists(installed) ? installed : null;
    }

    /// <summary>Opens a folder in Total Git (the running one shows it in a tab).</summary>
    public static void OpenInTotalGit(string exe, string folder)
    {
        Process.Start(new ProcessStartInfo(exe, GitRunner.Quote(folder)) { UseShellExecute = false, WorkingDirectory = folder })?.Dispose();
    }

    /// <summary>Opens Total Git's merge window on a conflicted file and waits for it to close.</summary>
    public static async Task<(int Exit, string Message)> MergeAsync(string exe, string file)
    {
        var psi = new ProcessStartInfo(exe, "--merge " + GitRunner.Quote(file))
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(file) ?? "",
        };
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ((int)MergeExit.Usage, ex.Message);
        }
        var stderr = process.StandardError.ReadToEndAsync();
        // The merge window stays open as long as it takes.
        await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
        return (process.ExitCode, (await stderr.ConfigureAwait(false)).Trim());
    }

    /// <summary>Opens a folder in a new VS Code window.</summary>
    public static bool OpenInVsCode(string folder)
    {
        var exe = FindVsCode();
        if (exe is null) return false;
        var psi = new ProcessStartInfo(exe, "--new-window " + GitRunner.Quote(folder)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        Process.Start(psi)?.Dispose();
        return true;
    }

    private static string? FindVsCode()
    {
        string[] roots =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code"),
        };
        foreach (var root in roots)
        {
            var exe = Path.Combine(root, "Code.exe");
            if (File.Exists(exe)) return exe;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var cli = Path.Combine(dir.Trim('"'), "code.cmd");
                if (!File.Exists(cli)) continue;
                var exe = Path.GetFullPath(Path.Combine(dir.Trim('"'), "..", "Code.exe"));
                return File.Exists(exe) ? exe : null;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
