namespace TotalGit.Core.Diagnostics;

/// <summary>
/// Total Git's log: errors shown to the user, failed git commands and crashes, kept for error reports. It does
/// nothing until <see cref="Configure"/> is called, so tests and tools don't write one.
/// </summary>
public static class DiagnosticLog
{
    /// <summary>The log is started afresh (the old one kept as <c>totalgit.old.log</c>) past this size.</summary>
    private const long MaxBytes = 512 * 1024;

    private static readonly Lock Gate = new();
    private static string? _path;

    public static string? FilePath => _path;

    /// <summary>Logs to <c>totalgit.log</c> in <paramref name="folder"/>; null stops logging.</summary>
    public static void Configure(string? folder)
    {
        if (folder is not null) Directory.CreateDirectory(folder);
        _path = folder is null ? null : Path.Combine(folder, "totalgit.log");
    }

    /// <summary>Adds a dated entry; later lines of a multi-line message are indented under it.</summary>
    public static void Write(string message)
    {
        if (_path is not { } path) return;
        var lines = message.TrimEnd().Replace("\r\n", "\n").Split('\n');
        var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {lines[0]}" + string.Concat(lines.Skip(1).Select(l => $"{Environment.NewLine}    {l}")) +
                    Environment.NewLine;
        lock (Gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);
                // Shared, as the merge window (another process) writes here too.
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream);
                writer.Write(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never be the thing that fails.
            }
        }
    }

    /// <summary>The last <paramref name="count"/> lines of the log (none when there isn't one).</summary>
    public static IReadOnlyList<string> Tail(int count)
    {
        if (_path is not { } path) return [];
        lock (Gate)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var lines = new Queue<string>(count + 1);
                while (reader.ReadLine() is { } line)
                {
                    lines.Enqueue(line);
                    if (lines.Count > count) lines.Dequeue();
                }
                return lines.ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }
}
