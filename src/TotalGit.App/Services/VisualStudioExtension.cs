using System.Diagnostics;
using TotalGit.Core.Projects;

namespace TotalGit.App.Services;

/// <summary>
/// The Total Git extension for Visual Studio (a Worktrees window, open in Total Git), which every release carries as
/// TotalGit.VisualStudio.vsix beside the app, built from the same commit. Visual Studio's own installer
/// (VSIXInstaller) puts it in; it can only finish while Visual Studio is closed.
/// </summary>
public static class VisualStudioExtension
{
    public const string Id = "paulepath.TotalGit.VisualStudio";

    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "TotalGit.VisualStudio.vsix");

    /// <summary>False when running from source, which has no .vsix beside it.</summary>
    public static bool IsBundled => File.Exists(BundledPath);

    /// <summary>The same version as the app's (and the VS Code extension's).</summary>
    public static Version BundledVersion => VsCodeExtension.BundledVersion;

    /// <summary>A separate Visual Studio instance to use instead of the normal one (e.g. "Exp", for testing).</summary>
    private static string RootSuffix => Environment.GetEnvironmentVariable("TOTALGIT_VS_ROOT_SUFFIX") ?? "";

    public static Version? InstalledVersion() => VisualStudioExtensions.InstalledVersion(VisualStudioExtensions.DefaultRoot(), Id, RootSuffix);

    /// <summary>Visual Studio's extension installer, from the newest Visual Studio installed; null without one.</summary>
    public static string? FindInstaller()
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        try
        {
            var psi = new ProcessStartInfo(vswhere) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            // [17.0,) is Visual Studio 2022 or later.
            foreach (var a in new[] { "-latest", "-version", "[17.0,)", "-property", "installationPath" }) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi);
            if (process is null) return null;
            var path = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10000);
            var installer = Path.Combine(path, "Common7", "IDE", "VSIXInstaller.exe");
            return path.Length > 0 && File.Exists(installer) ? installer : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>A Visual Studio is running (its installer can't finish until it closes).</summary>
    public static bool VisualStudioRunning()
    {
        var running = Process.GetProcessesByName("devenv");
        foreach (var p in running) p.Dispose();
        return running.Length > 0;
    }

    /// <summary>Opens Visual Studio's extension installer on the bundled extension; it asks to close Visual Studio if needed.</summary>
    public static void StartInstall(string installer)
    {
        var psi = new ProcessStartInfo(installer) { UseShellExecute = false };
        if (RootSuffix.Length > 0) psi.ArgumentList.Add($"/rootSuffix:{RootSuffix}");
        psi.ArgumentList.Add(BundledPath);
        Process.Start(psi)?.Dispose();
    }

    /// <summary>
    /// Updates the extension when an older version is installed and no Visual Studio is open, quietly (it's this
    /// release's). Never installs it where it isn't. With Visual Studio open it waits for a later start.
    /// </summary>
    public static async Task UpdateIfOlderAsync()
    {
        try
        {
            if (!IsBundled || InstalledVersion() is not { } installed || installed >= BundledVersion || VisualStudioRunning()) return;
            if (FindInstaller() is not { } installer) return;
            var psi = new ProcessStartInfo(installer) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("/quiet");
            if (RootSuffix.Length > 0) psi.ArgumentList.Add($"/rootSuffix:{RootSuffix}");
            psi.ArgumentList.Add(BundledPath);
            using var process = Process.Start(psi);
            if (process is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) Trace.WriteLine($"Updating the Visual Studio extension failed (exit code {process.ExitCode}).");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException
                                       or UnauthorizedAccessException or OperationCanceledException)
        {
            Trace.WriteLine($"Updating the Visual Studio extension failed: {ex.Message}");
        }
    }
}
