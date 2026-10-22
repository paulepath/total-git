using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TotalGit.App.Services;

/// <summary>
/// Keeps to one Total Git per settings folder: starting it again (e.g. "Open in Total Git" from VS Code) hands the
/// folder to the copy already running, which opens it in a tab, and exits. Copies with their own TOTALGIT_DATA_DIR
/// (tests, demos) have their own pipe, so they never talk to each other.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Ack = "ok";

    /// <summary>A message asking to open a pull request (from a notification) rather than a folder.</summary>
    private const string PullRequestPrefix = "pr:";

    private readonly CancellationTokenSource _stop = new();

    private SingleInstance() { }

    private static string PipeName { get; } = "TotalGit-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(AppSettings.DataDirectory).ToLowerInvariant())))[..16];

    /// <summary>
    /// Hands <paramref name="path"/> (a folder to open, or null just to bring the window up), or a pull request's
    /// address from a notification, to a running Total Git. Returns true if one took it; false if none is running (or
    /// it didn't answer), so this one starts.
    /// </summary>
    public static bool TryForward(string? path, string? pullRequestUrl = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(300);
            // We were just started by the user, so may bring a window forward; let the running copy do it instead.
            if (OperatingSystem.IsWindows() && GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out var pid))
                AllowSetForegroundWindow(pid);

            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            writer.WriteLine(pullRequestUrl is not null ? PullRequestPrefix + pullRequestUrl : path is null ? "" : Path.GetFullPath(path));
            var answer = reader.ReadLineAsync();
            return answer.Wait(TimeSpan.FromSeconds(5)) && answer.Result == Ack;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or AggregateException)
        {
            return false;
        }
    }

    /// <summary>The pull request address in a message from <see cref="Listen"/>, or null for a folder.</summary>
    public static string? PullRequestUrl(string? message) =>
        message?.StartsWith(PullRequestPrefix, StringComparison.Ordinal) == true ? message[PullRequestPrefix.Length..] : null;

    /// <summary>
    /// Listens for later starts; <paramref name="open"/> gets each one's folder (or null, or a pull request: see
    /// <see cref="PullRequestUrl"/>) on a pool thread.
    /// </summary>
    public static SingleInstance Listen(Action<string?> open)
    {
        var listener = new SingleInstance();
        _ = listener.RunAsync(open);
        return listener;
    }

    private async Task RunAsync(Action<string?> open)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var line = await reader.ReadLineAsync(timeout.Token);
                if (line is null) continue;

                open(line.Length == 0 ? null : line);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(Ack);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // A caller that went away, or (briefly) another copy holding the name: carry on listening.
                Trace.WriteLine($"Single-instance pipe: {ex.Message}");
                try
                {
                    await Task.Delay(500, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
