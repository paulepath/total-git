using TotalGit.Core.Diagnostics;

namespace TotalGit.Core.Tests;

public sealed class ErrorReportTests
{
    private static readonly ReportEnvironment Env = new("0.3.50", "10.0.26300.0", "git version 2.51.0.windows.1");

    [Fact]
    public void Error_report_has_the_error_versions_and_log()
    {
        var report = new ErrorReport("Commit 0000000 is not in the loaded history.", null, Env, ["10:00 git fetch failed"], null);

        Assert.Equal("Commit 0000000 is not in the loaded history.", report.Title);
        Assert.Contains("Commit 0000000 is not in the loaded history.", report.Body);
        Assert.Contains("Total Git 0.3.50", report.Body);
        Assert.Contains("git version 2.51.0.windows.1", report.Body);
        Assert.Contains("10:00 git fetch failed", report.Body);
        Assert.StartsWith(ErrorReport.NewIssueUrl + "?title=Commit%200000000", report.ToUrl());
    }

    [Fact]
    public void Crash_title_is_the_exception_line()
    {
        var report = new ErrorReport(null, "System.InvalidOperationException: Sequence contains no elements\n   at Foo()", Env, [], null);

        Assert.Equal("Crash: System.InvalidOperationException: Sequence contains no elements", report.Title);
        Assert.Contains("   at Foo()", report.Body);
    }

    [Fact]
    public void Home_folder_and_credentials_are_blanked()
    {
        const string home = @"C:\Users\someone";
        var text = ErrorReport.Redact(
            @"fatal: C:\Users\someone\code\app and c:/users/someone/x; https://someone:secret@github.com/a/b.git; token ghp_abcdefghijklmnopqrstuvwxyz0123", home);

        Assert.Equal(@"fatal: ~\code\app and ~/x; https://***@github.com/a/b.git; token ***", text);
    }

    [Fact]
    public void Long_log_is_cut_to_fit_the_link_but_kept_whole_in_the_details()
    {
        var log = Enumerable.Range(0, 400).Select(i => $"2026-10-20 10:00:{i % 60:00} line {i} " + new string('x', 80)).ToList();
        var report = new ErrorReport("Push failed", null, Env, log, null);

        var url = report.ToUrl();
        Assert.True(url.Length <= ErrorReport.MaxUrlLength, $"{url.Length} characters");
        Assert.Contains("line%20399", url); // the newest lines are the ones kept
        Assert.DoesNotContain("line%200%20", url);
        Assert.Contains("line 0 ", report.Body);
    }

    [Fact]
    public void Huge_error_is_cut_short_to_fit()
    {
        var report = new ErrorReport(new string('é', 20_000), null, Env, [], null);

        Assert.True(report.ToUrl().Length <= ErrorReport.MaxUrlLength);
    }
}

[Collection(nameof(DiagnosticLogTests))] // the log is static: one test at a time
public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "totalgit-tests", "log-" + Guid.NewGuid().ToString("N")[..8]);

    public DiagnosticLogTests() => DiagnosticLog.Configure(_folder);

    public void Dispose()
    {
        DiagnosticLog.Configure(null);
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    [Fact]
    public void Writes_dated_entries_with_later_lines_indented()
    {
        var marker = Guid.NewGuid().ToString("N");
        DiagnosticLog.Write($"git push failed {marker}\nremote: rejected");

        // Other tests' failing git commands can land in the log meanwhile, so find this entry rather than count lines.
        var tail = DiagnosticLog.Tail(200).ToList();
        var i = tail.FindIndex(l => l.EndsWith(marker, StringComparison.Ordinal));
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d git push failed ", tail[i]);
        Assert.Equal("    remote: rejected", tail[i + 1]);
    }

    [Fact]
    public void Starts_afresh_past_its_size_limit_keeping_the_old_log()
    {
        var line = new string('x', 1000);
        for (var i = 0; i < 600; i++) DiagnosticLog.Write($"{i} {line}");

        Assert.True(File.Exists(Path.Combine(_folder, "totalgit.old.log")));
        Assert.True(new FileInfo(DiagnosticLog.FilePath!).Length < 600 * 1000);
        Assert.Contains(DiagnosticLog.Tail(50), l => l.Contains(" 599 x", StringComparison.Ordinal));
    }
}
