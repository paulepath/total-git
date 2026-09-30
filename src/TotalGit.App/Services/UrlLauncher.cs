using System.Diagnostics;

namespace TotalGit.App.Services;

public static class UrlLauncher
{
    /// <summary>Opens a web page in the default browser.</summary>
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser registered; nothing useful to do.
        }
    }
}
