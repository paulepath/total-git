using System.Diagnostics;

namespace TotalGit.App.Services;

/// <summary>Finds and launches Visual Studio Code on a folder.</summary>
public static class VsCodeLauncher
{
    /// <summary>Opens <paramref name="folder"/> in a new VS Code window.</summary>
    public static void Open(string folder, string? configuredPath = null) =>
        Run(configuredPath, folder, ["--new-window", folder]);

    /// <summary>
    /// Opens a file, optionally at a line and column. The worktree folder is passed too, so a VS Code window that
    /// already has it open is reused (and focused); otherwise a new window opens on the folder.
    /// </summary>
    public static void OpenFile(string folder, string file, int? line = null, int? column = null, string? configuredPath = null)
    {
        var target = line is { } l ? $"{file}:{l}" + (column is { } c ? $":{c}" : "") : file;
        Run(configuredPath, folder, [folder, "--goto", target]);
    }

    private static void Run(string? configuredPath, string workingDirectory, string[] args)
    {
        var exe = Resolve(configuredPath)
            ?? throw new FileNotFoundException("Visual Studio Code was not found. Set its path in settings.json (VsCodePath).");

        ProcessStartInfo psi;
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // /s makes cmd strip only the outer quotes, so paths with spaces survive.
            var quoted = string.Join(' ', args.Select(a => a.StartsWith('-') ? a : $"\"{a}\""));
            psi = new ProcessStartInfo("cmd.exe") { Arguments = $"/s /c \"\"{exe}\" {quoted}\"" };
        }
        else
        {
            psi = new ProcessStartInfo(exe);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = workingDirectory;
        Process.Start(psi)?.Dispose();
    }

    /// <summary>
    /// Runs VS Code's command line (e.g. <c>--install-extension</c>) and waits for it. Returns its exit code and output.
    /// </summary>
    public static async Task<(int Exit, string Output)> RunCliAsync(string? configuredPath, params string[] args)
    {
        var cli = ResolveCli(configuredPath)
            ?? throw new FileNotFoundException("Visual Studio Code was not found. Set its path in settings.json (VsCodePath).");
        ProcessStartInfo psi;
        if (cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var quoted = string.Join(' ', args.Select(a => a.StartsWith('-') ? a : $"\"{a}\""));
            psi = new ProcessStartInfo("cmd.exe") { Arguments = $"/s /c \"\"{cli}\" {quoted}\"" };
        }
        else
        {
            psi = new ProcessStartInfo(cli);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using var process = Process.Start(psi) ?? throw new FileNotFoundException($"Couldn't start {cli}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("VS Code's command line didn't finish within two minutes.");
        }
        return (process.ExitCode, ((await output) + (await error)).Trim());
    }

    /// <summary>
    /// VS Code's command-line script: Code.exe itself starts the editor, so commands like --install-extension go
    /// through bin\code.cmd beside it.
    /// </summary>
    public static string? ResolveCli(string? configuredPath = null)
    {
        if (Resolve(configuredPath) is not { } found) return null;
        if (OperatingSystem.IsWindows() && Path.GetFileName(found).Equals("Code.exe", StringComparison.OrdinalIgnoreCase))
        {
            var cli = Path.Combine(Path.GetDirectoryName(found)!, "bin", "code.cmd");
            return File.Exists(cli) ? cli : null;
        }
        return found;
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
