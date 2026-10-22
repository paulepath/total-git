using TotalGit.Core.Diagnostics;
using TotalGit.Core.Git;

namespace TotalGit.App.Services;

/// <summary>
/// Error reports: a GitHub issue filled in with the error, versions and the end of the log, which the user reads and
/// submits in their browser. Also saves a crash, so the next start can offer to report it.
/// </summary>
public static class ErrorReporter
{
    private const int LogLines = 60;

    private static string CrashFile => Path.Combine(AppSettings.DataDirectory, "crash.txt");

    /// <summary>Set at start-up (the installed version, or "dev").</summary>
    public static string AppVersion { get; set; } = "dev";

    /// <summary>Starts the log and catches crashes. Call once, early.</summary>
    public static void Start()
    {
        DiagnosticLog.Configure(Path.Combine(AppSettings.DataDirectory, "logs"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => SaveCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => DiagnosticLog.Write($"Unobserved task error: {e.Exception}");
    }

    /// <summary>Writes the error that's closing Total Git where the next start will find it.</summary>
    private static void SaveCrash(Exception? ex)
    {
        var text = ex?.ToString() ?? "Unknown error";
        DiagnosticLog.Write($"Crash: {text}");
        try { File.WriteAllText(CrashFile, $"Total Git {AppVersion}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{text}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The crash saved by the last run, if it ended in one; taken, so it's offered only once.</summary>
    public static string? TakeCrash()
    {
        try
        {
            if (!File.Exists(CrashFile)) return null;
            var text = File.ReadAllText(CrashFile);
            File.Move(CrashFile, Path.Combine(AppSettings.DataDirectory, "crash-last.txt"), overwrite: true);
            return text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task<ErrorReport> CreateAsync(string? error, string? crash = null)
    {
        string? git = null;
        try
        {
            var result = await GitCli.RunAsync(Path.GetTempPath(), ["--version"], throwOnError: false);
            if (result.ExitCode == 0) git = result.StdOut.Trim();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        var environment = new ReportEnvironment(AppVersion, Environment.OSVersion.Version.ToString(), git);
        return new ErrorReport(error, crash, environment, DiagnosticLog.Tail(LogLines),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>Opens the filled-in GitHub issue in the browser, for the user to check and submit.</summary>
    public static async Task OpenIssueAsync(string? error, string? crash = null) =>
        UrlLauncher.Open((await CreateAsync(error, crash)).ToUrl());

    /// <summary>The whole report as text, for pasting into an email or chat instead.</summary>
    public static async Task<string> DetailsAsync(string? error, string? crash = null)
    {
        var report = await CreateAsync(error, crash);
        return $"{report.Title}\n\n{report.Body}";
    }
}
