using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TotalGit.VisualStudio.Git;

internal sealed class GitException(string message) : Exception(message);

/// <summary>Runs git (from PATH, else the copy that comes with Visual Studio) and returns its output.</summary>
internal static class GitRunner
{
    private static readonly Lazy<string> GitPath = new(FindGit);

    public static async Task<string> RunAsync(string folder, string arguments, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(GitPath.Value, arguments)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(true);
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GitException($"Couldn't run git: {ex.Message}");
        }
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using (var timeout = new CancellationTokenSource(timeoutMs))
        using (timeout.Token.Register(() => exited.TrySetResult(false)))
        {
            if (!await exited.Task.ConfigureAwait(false))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                throw new GitException($"git {arguments} took too long.");
            }
        }
        var stdout = await output.ConfigureAwait(false);
        var stderr = await error.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new GitException(stderr.Trim().Length > 0 ? stderr.Trim() : $"git {arguments} failed ({process.ExitCode}).");
        return stdout;
    }

    /// <summary>Quotes an argument for a Windows command line.</summary>
    public static string Quote(string arg) => "\"" + arg.Replace("\"", "\\\"") + "\"";

    private static string FindGit()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "git.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        // Visual Studio's own git, next to devenv.exe.
        var ide = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? "") ?? "";
        var bundled = Path.Combine(ide, @"CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe");
        return File.Exists(bundled) ? bundled : "git.exe";
    }
}
