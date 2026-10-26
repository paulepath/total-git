using System.Text;
using System.Text.RegularExpressions;

namespace TotalGit.Core.Diagnostics;

/// <summary>Versions that go in every report.</summary>
public sealed record ReportEnvironment(string AppVersion, string WindowsVersion, string? GitVersion);

/// <summary>
/// An error report: a GitHub issue for the user to read and submit, filled in with the error, versions and the end of
/// the log. Paths under the user's home folder and anything that looks like a credential are blanked out.
/// </summary>
public sealed partial class ErrorReport
{
    public const string NewIssueUrl = "https://github.com/paulepath/total-git/issues/new";

    /// <summary>
    /// GitHub refuses longer links. Someone not signed in to GitHub is sent to its sign-in page with the whole new-issue
    /// link encoded again inside (<see cref="SignInUrl"/>), and that's what has to fit: past about 7,000 characters
    /// the sign-in page is an error page (measured October 2026). The log is shortened (oldest lines first) to fit.
    /// </summary>
    public const int MaxUrlLength = 6500;

    private const string SignInUrl = "https://github.com/login?return_to=";

    private const int StackLines = 40;

    private readonly string? _error;
    private readonly string? _crash;
    private readonly ReportEnvironment _environment;
    private readonly IReadOnlyList<string> _log;

    /// <param name="error">The message the user saw, if any.</param>
    /// <param name="crash">An exception that closed Total Git (its text), if this reports a crash.</param>
    /// <param name="home">The user's home folder, shown as <c>~</c>.</param>
    public ErrorReport(string? error, string? crash, ReportEnvironment environment, IReadOnlyList<string> log, string? home)
    {
        _error = string.IsNullOrWhiteSpace(error) ? null : Redact(error, home);
        _crash = string.IsNullOrWhiteSpace(crash) ? null : Redact(crash, home);
        _environment = environment;
        _log = log.Select(l => Redact(l, home)).ToList();
        Title = _crash is not null ? $"Crash: {FirstLine(_crash)}"
            : _error is not null ? FirstLine(_error)
            : "Problem report";
    }

    public string Title { get; }

    /// <summary>The whole report, as markdown (what "Copy details" puts on the clipboard).</summary>
    public string Body => BuildBody(_log.Count);

    /// <summary>The new-issue link, with as much of the log as fits.</summary>
    public string ToUrl()
    {
        // Fewer log lines until it fits; halving keeps this quick for a long log.
        var lines = _log.Count;
        var url = Url(BuildBody(lines));
        while (SignInLength(url) > MaxUrlLength && lines > 0)
        {
            lines /= 2;
            url = Url(BuildBody(lines));
        }
        // Still too long (a huge error message): cut the end off; the user can paste the rest from "Copy details".
        var cut = BuildBody(0);
        while (SignInLength(url) > MaxUrlLength && cut.Length > 0)
        {
            cut = cut[..Math.Max(0, cut.Length - Math.Max(50, (SignInLength(url) - MaxUrlLength) / 6))];
            url = Url(cut + "\n\n(cut short)");
        }
        return url;
    }

    /// <summary>The length of GitHub's sign-in link that carries <paramref name="url"/>, which is longer than the link itself.</summary>
    public static int SignInLength(string url) => SignInUrl.Length + Uri.EscapeDataString(url).Length;

    private string Url(string body) =>
        $"{NewIssueUrl}?title={Uri.EscapeDataString(Title)}&body={Uri.EscapeDataString(body)}";

    private string BuildBody(int logLines)
    {
        var b = new StringBuilder();
        b.AppendLine("**What were you doing?**").AppendLine().AppendLine("<!-- A sentence or two helps. -->").AppendLine();
        if (_error is not null) Block(b, "Error", _error);
        if (_crash is not null) Block(b, "Crash", string.Join('\n', _crash.Replace("\r\n", "\n").Split('\n').Take(StackLines)));
        b.AppendLine("**Versions**").AppendLine();
        b.AppendLine($"- Total Git {_environment.AppVersion}");
        b.AppendLine($"- Windows {_environment.WindowsVersion}");
        b.AppendLine($"- {_environment.GitVersion ?? "git not found"}");
        if (logLines > 0)
        {
            b.AppendLine().AppendLine("<details><summary>Recent log</summary>").AppendLine();
            b.AppendLine("```");
            foreach (var line in _log.Skip(_log.Count - logLines)) b.AppendLine(line);
            b.AppendLine("```").AppendLine("</details>");
        }
        return b.ToString().Replace("\r\n", "\n");
    }

    private static void Block(StringBuilder b, string heading, string text) =>
        b.AppendLine($"**{heading}**").AppendLine().AppendLine("```").AppendLine(text.Trim()).AppendLine("```").AppendLine();

    private static string FirstLine(string text)
    {
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length <= 100 ? line : line[..99] + "…";
    }

    /// <summary>The home folder becomes <c>~</c>; passwords and tokens in URLs, and GitHub tokens, become <c>***</c>.</summary>
    public static string Redact(string text, string? home)
    {
        if (!string.IsNullOrEmpty(home))
        {
            var trimmed = home.TrimEnd('\\', '/');
            text = text.Replace(trimmed, "~", StringComparison.OrdinalIgnoreCase)
                .Replace(trimmed.Replace('\\', '/'), "~", StringComparison.OrdinalIgnoreCase);
        }
        text = UrlCredentials().Replace(text, "://***@");
        return GitHubToken().Replace(text, "***");
    }

    [GeneratedRegex(@"://[^/@\s]+@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})")]
    private static partial Regex GitHubToken();
}
