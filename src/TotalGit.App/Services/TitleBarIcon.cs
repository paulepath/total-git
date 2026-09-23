using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace TotalGit.App.Services;

/// <summary>
/// Windows shows one icon in the taskbar (the big icon) and another in the title bar (the small
/// icon). Avalonia sets both from Window.Icon (the light, transparent app icon), so this swaps the
/// small one for the dark-tile version, which stands out against the app's dark title bar.
/// </summary>
public static class TitleBarIcon
{
    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const int SM_CXSMICON = 49;

    private static readonly Lazy<string?> IconFile = new(ExtractIcon);

    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindows() || IconFile.Value is not { } file) return;
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;

        var size = (int)Math.Round(GetSystemMetrics(SM_CXSMICON) * window.RenderScaling);
        var icon = LoadImage(IntPtr.Zero, file, IMAGE_ICON, size, size, LR_LOADFROMFILE);
        if (icon != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, ICON_SMALL, icon);
    }

    // LoadImage needs a file; copy the embedded .ico out once per run.
    private static string? ExtractIcon()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"totalgit-titlebar-{Environment.ProcessId}.ico");
            using (var src = AssetLoader.Open(new Uri("avares://TotalGit/Assets/totalgit-dark.ico")))
            using (var dst = File.Create(path))
                src.CopyTo(dst);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            };
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, nint wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
