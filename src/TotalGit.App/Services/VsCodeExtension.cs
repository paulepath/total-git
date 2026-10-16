using System.Diagnostics;
using TotalGit.Core.Projects;

namespace TotalGit.App.Services;

/// <summary>
/// The Total Git extension for VS Code (worktrees in VS Code, open in Total Git), which every release carries as
/// totalgit.vsix beside the app, built from the same commit.
/// </summary>
public static class VsCodeExtension
{
    public const string Id = "paulepath.totalgit";

    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "totalgit.vsix");

    /// <summary>False when running from source, which has no .vsix beside it.</summary>
    public static bool IsBundled => File.Exists(BundledPath);

    /// <summary>The bundled extension's version: the app's own (both are stamped 0.3.&lt;run&gt; by the release build).</summary>
    public static Version BundledVersion
    {
        get
        {
            var v = typeof(VsCodeExtension).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>The version installed in VS Code, or null if it isn't.</summary>
    public static Version? InstalledVersion() => VsCodeExtensions.InstalledVersion(VsCodeExtensions.DefaultFolder(), Id);

    /// <summary>Installs (or replaces) the bundled extension with VS Code's command line.</summary>
    public static async Task InstallAsync(string? vsCodePath)
    {
        var (exit, output) = await VsCodeLauncher.RunCliAsync(vsCodePath, "--install-extension", BundledPath, "--force");
        if (exit != 0) throw new IOException(output.Length > 0 ? output : $"VS Code's command line failed (exit code {exit}).");
    }

    /// <summary>
    /// Updates the extension when an older version is installed, quietly (it's this release's). Never installs it
    /// where it isn't: someone who uninstalled it stays without it.
    /// </summary>
    public static async Task UpdateIfOlderAsync(string? vsCodePath)
    {
        try
        {
            if (!IsBundled || InstalledVersion() is not { } installed || installed >= BundledVersion) return;
            await InstallAsync(vsCodePath);
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or TimeoutException or System.ComponentModel.Win32Exception
                                       or UnauthorizedAccessException or InvalidOperationException)
        {
            Trace.WriteLine($"Updating the VS Code extension failed: {ex.Message}");
        }
    }
}
