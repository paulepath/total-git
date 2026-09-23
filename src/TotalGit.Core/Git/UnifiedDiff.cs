using System.Text.RegularExpressions;

namespace TotalGit.Core.Git;

public enum DiffLineKind
{
    Hunk,
    Context,
    Added,
    Removed,
    NoNewline,
}

public readonly record struct DiffLine(DiffLineKind Kind, int? OldLine, int? NewLine, string Text);

/// <summary>Parses unified diff text (as produced by git / libgit2) into numbered lines.</summary>
public static partial class UnifiedDiff
{
    public const int MaxLines = 10_000;

    [GeneratedRegex(@"^@@ -(?<old>\d+)(?:,\d+)? \+(?<new>\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    public static (IReadOnlyList<DiffLine> Lines, bool Truncated) Parse(string patch, int maxLines = MaxLines)
    {
        var lines = new List<DiffLine>();
        int oldNo = 0, newNo = 0;
        var inHunk = false;

        foreach (var raw in patch.Split('\n'))
        {
            if (lines.Count >= maxLines) return (lines, true);
            var line = raw.TrimEnd('\r');

            var hunk = HunkHeader().Match(line);
            if (hunk.Success)
            {
                oldNo = int.Parse(hunk.Groups["old"].Value);
                newNo = int.Parse(hunk.Groups["new"].Value);
                inHunk = true;
                lines.Add(new DiffLine(DiffLineKind.Hunk, null, null, line));
                continue;
            }
            if (!inHunk || line.Length == 0) continue;

            switch (line[0])
            {
                case '+':
                    lines.Add(new DiffLine(DiffLineKind.Added, null, newNo++, line[1..]));
                    break;
                case '-':
                    lines.Add(new DiffLine(DiffLineKind.Removed, oldNo++, null, line[1..]));
                    break;
                case ' ':
                    lines.Add(new DiffLine(DiffLineKind.Context, oldNo++, newNo++, line[1..]));
                    break;
                case '\\':
                    lines.Add(new DiffLine(DiffLineKind.NoNewline, null, null, line));
                    break;
                default:
                    // A new file header ("diff --git") ends the hunk.
                    inHunk = false;
                    break;
            }
        }
        return (lines, false);
    }
}
