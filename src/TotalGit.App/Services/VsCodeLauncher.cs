using System.Diagnostics;

namespace TotalGit.App.Services;

/// <summary>Finds and launches Visual Studio Code on a folder.</summary>
public static class VsCodeLauncher
{
    /// <summary>Opens <paramref name="folder"/> in a new VS Code window.</summary>
    public static void Open(string folder, string? configuredPath = null)
    {
        var exe = Resolve(configuredPath)
            ?? throw new FileNotFoundException("Visual Studio Code was not found. Set its path in settings.json (VsCodePath).");

        ProcessStartInfo psi;
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // /s makes cmd strip only the outer quotes, so paths with spaces survive.
            psi = new ProcessStartInfo("cmd.exe") { Arguments = $"/s /c \"\"{exe}\" --new-window \"{folder}\"\"" };
        }
        else
        {
            psi = new ProcessStartInfo(exe);
            psi.ArgumentList.Add("--new-window");
            psi.ArgumentList.Add(folder);
        }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = folder;
        Process.Start(psi)?.Dispose();
    }

    public static string? Resolve(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return configuredPath;

        var names = OperatingSystem.IsWindows() ? new[] { "code.cmd", "code" } : ["code"];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return PreferExe(candidate);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            string[] roots =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code"),
            ];
            foreach (var root in roots)
            {
                var exe = Path.Combine(root, "Code.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            const string mac = "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code";
            if (File.Exists(mac)) return mac;
        }
        return null;
    }

    /// <summary>On Windows, bin\code.cmd sits next to ..\Code.exe; launching the exe avoids a console.</summary>
    private static string PreferExe(string cli)
    {
        if (!OperatingSystem.IsWindows()) return cli;
        var exe = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cli)!, "..", "Code.exe"));
        return File.Exists(exe) ? exe : cli;
    }
}
