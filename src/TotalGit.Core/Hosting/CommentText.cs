using System.Net;
using System.Text.RegularExpressions;

namespace TotalGit.Core.Hosting;

/// <summary>Plain text from comments' Markdown, for one-line summaries (a folded thread's title).</summary>
public static partial class CommentText
{
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"<details\b.*?</details\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Details();

    [GeneratedRegex(@"^(`{3,}|~{3,}).*?^\1", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex CodeFence();

    [GeneratedRegex(@"^\s*(\*\*|__)(?<text>.+?)\1\s*$")]
    private static partial Regex BoldLine();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[(?<text>[^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"^\s{0,3}(#{1,6}\s+|>\s?|[-*+]\s+(\[[ xX]\]\s+)?|\d+[.)]\s+)")]
    private static partial Regex LinePrefix();

    // Emphasis markers wrapping words (so snake_case names keep their underscores), and code ticks.
    [GeneratedRegex(@"(?<!\w)(\*\*|__|\*|_|~~)(?=\S)(?<text>.+?)(?<=\S)\1(?!\w)")]
    private static partial Regex Emphasis();

    /// <summary>
    /// A comment's title: its first line that is wholly bold (review bots lead with one, after a line of labels),
    /// otherwise its first line of text. Markdown, HTML and folded sections are removed. Empty when there's no text.
    /// </summary>
    public static string Title(string body, int maxLength = 120)
    {
        var text = Details().Replace(HtmlComment().Replace(body.Replace("\r\n", "\n"), ""), "");
        text = CodeFence().Replace(text, "");
        var lines = text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        var bold = lines.Select(l => BoldLine().Match(l)).FirstOrDefault(m => m.Success);
        var line = bold?.Groups["text"].Value ?? lines.Select(Plain).FirstOrDefault(l => l.Length > 0) ?? "";
        line = Plain(line);
        return line.Length > maxLength ? line[..(maxLength - 1)].TrimEnd() + "…" : line;
    }

    /// <summary>One line of Markdown as plain text.</summary>
    public static string Plain(string line)
    {
        var text = Image().Replace(line, "");
        text = Link().Replace(text, m => m.Groups["text"].Value);
        text = Tag().Replace(text, "");
        text = LinePrefix().Replace(text, "");
        text = Emphasis().Replace(text, m => m.Groups["text"].Value).Replace("`", "");
        return Regex.Replace(WebUtility.HtmlDecode(text), @"\s+", " ").Trim();
    }
}
