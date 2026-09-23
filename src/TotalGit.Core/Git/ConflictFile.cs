namespace TotalGit.Core.Git;

public enum ConflictChoice
{
    Unresolved,
    Ours,
    Theirs,
    OursThenTheirs,
    TheirsThenOurs,
}

/// <summary>A run of lines in a conflicted file: either shared text or one conflict.</summary>
public abstract record ConflictSegment;

public sealed record CommonSegment(IReadOnlyList<string> Lines) : ConflictSegment;

/// <param name="Base">The common ancestor's lines, when git wrote diff3-style markers.</param>
public sealed record ConflictHunk(
    IReadOnlyList<string> Ours,
    IReadOnlyList<string>? Base,
    IReadOnlyList<string> Theirs,
    string OursLabel,
    string TheirsLabel,
    string? BaseLabel) : ConflictSegment;

/// <summary>Where each conflict sits in one side's full text (for highlighting).</summary>
public sealed record ConflictSide(IReadOnlyList<string> Lines, IReadOnlyList<(int Start, int Count)> Conflicts);

/// <summary>
/// A working-tree file containing git conflict markers, split into shared text and conflicts so
/// each conflict can be resolved by picking a side.
/// </summary>
public sealed class ConflictFile
{
    private const string OursMarker = "<<<<<<<";
    private const string BaseMarker = "|||||||";
    private const string SplitMarker = "=======";
    private const string TheirsMarker = ">>>>>>>";

    private ConflictFile(IReadOnlyList<ConflictSegment> segments, string newLine, bool endsWithNewLine)
    {
        Segments = segments;
        NewLine = newLine;
        EndsWithNewLine = endsWithNewLine;
        Conflicts = segments.OfType<ConflictHunk>().ToList();
    }

    public IReadOnlyList<ConflictSegment> Segments { get; }
    public IReadOnlyList<ConflictHunk> Conflicts { get; }
    public string NewLine { get; }
    public bool EndsWithNewLine { get; }

    /// <summary>The label git wrote after <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> (HEAD, or the commit a rebase is on).</summary>
    public string OursLabel => Conflicts.FirstOrDefault()?.OursLabel ?? "ours";

    /// <summary>The label git wrote after <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> (the branch being merged, or the commit being replayed).</summary>
    public string TheirsLabel => Conflicts.FirstOrDefault()?.TheirsLabel ?? "theirs";

    public static ConflictFile Parse(string text)
    {
        var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
        var endsWithNewLine = lines.Count > 0 && lines[^1].Length == 0 && text.Length > 0;
        if (endsWithNewLine) lines.RemoveAt(lines.Count - 1);

        var segments = new List<ConflictSegment>();
        var common = new List<string>();
        var i = 0;
        while (i < lines.Count)
        {
            if (IsMarker(lines[i], OursMarker) && TryReadConflict(lines, i, out var hunk, out var next))
            {
                if (common.Count > 0) segments.Add(new CommonSegment(common));
                common = [];
                segments.Add(hunk);
                i = next;
                continue;
            }
            common.Add(lines[i]);
            i++;
        }
        if (common.Count > 0) segments.Add(new CommonSegment(common));
        return new ConflictFile(segments, newLine, endsWithNewLine);
    }

    private static bool IsMarker(string line, string marker) =>
        line.StartsWith(marker, StringComparison.Ordinal) && (line.Length == marker.Length || line[marker.Length] == ' ');

    private static string Label(string line, string marker) => line.Length > marker.Length ? line[(marker.Length + 1)..] : "";

    private static bool TryReadConflict(List<string> lines, int start, out ConflictHunk hunk, out int next)
    {
        hunk = null!;
        next = start;
        var ours = new List<string>();
        List<string>? @base = null;
        var theirs = new List<string>();
        string? baseLabel = null;
        var section = 0; // 0 ours, 1 base, 2 theirs
        for (var i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (section == 0 && IsMarker(line, BaseMarker))
            {
                section = 1;
                @base = [];
                baseLabel = Label(line, BaseMarker);
            }
            else if (section < 2 && line == SplitMarker)
            {
                section = 2;
            }
            else if (section == 2 && IsMarker(line, TheirsMarker))
            {
                hunk = new ConflictHunk(ours, @base, theirs, Label(lines[start], OursMarker), Label(line, TheirsMarker), baseLabel);
                next = i + 1;
                return true;
            }
            else if (IsMarker(line, OursMarker))
            {
                return false; // nested or malformed: leave it as plain text
            }
            else
            {
                (section switch { 0 => ours, 1 => @base!, _ => theirs }).Add(line);
            }
        }
        return false;
    }

    /// <summary>The file with each conflict replaced by its choice; unresolved conflicts keep their markers.</summary>
    public string Render(IReadOnlyList<ConflictChoice> choices)
    {
        var lines = new List<string>();
        var n = 0;
        foreach (var segment in Segments)
        {
            if (segment is CommonSegment c)
            {
                lines.AddRange(c.Lines);
                continue;
            }
            var hunk = (ConflictHunk)segment;
            var choice = n < choices.Count ? choices[n] : ConflictChoice.Unresolved;
            n++;
            switch (choice)
            {
                case ConflictChoice.Ours: lines.AddRange(hunk.Ours); break;
                case ConflictChoice.Theirs: lines.AddRange(hunk.Theirs); break;
                case ConflictChoice.OursThenTheirs: lines.AddRange(hunk.Ours); lines.AddRange(hunk.Theirs); break;
                case ConflictChoice.TheirsThenOurs: lines.AddRange(hunk.Theirs); lines.AddRange(hunk.Ours); break;
                default:
                    lines.Add(hunk.OursLabel.Length > 0 ? $"{OursMarker} {hunk.OursLabel}" : OursMarker);
                    lines.AddRange(hunk.Ours);
                    if (hunk.Base is not null)
                    {
                        lines.Add(hunk.BaseLabel is { Length: > 0 } bl ? $"{BaseMarker} {bl}" : BaseMarker);
                        lines.AddRange(hunk.Base);
                    }
                    lines.Add(SplitMarker);
                    lines.AddRange(hunk.Theirs);
                    lines.Add(hunk.TheirsLabel.Length > 0 ? $"{TheirsMarker} {hunk.TheirsLabel}" : TheirsMarker);
                    break;
            }
        }
        return Join(lines);
    }

    /// <summary>One side's version of the whole file, with where each conflict is in it.</summary>
    public ConflictSide Side(bool ours)
    {
        var lines = new List<string>();
        var ranges = new List<(int, int)>();
        foreach (var segment in Segments)
        {
            if (segment is CommonSegment c)
            {
                lines.AddRange(c.Lines);
                continue;
            }
            var hunk = (ConflictHunk)segment;
            var side = ours ? hunk.Ours : hunk.Theirs;
            ranges.Add((lines.Count, side.Count));
            lines.AddRange(side);
        }
        return new ConflictSide(lines, ranges);
    }

    public string Join(IEnumerable<string> lines)
    {
        var text = string.Join(NewLine, lines);
        return EndsWithNewLine ? text + NewLine : text;
    }

    /// <summary>True if the text still contains a complete conflict marker block.</summary>
    public static bool HasMarkers(string text) => Parse(text).Conflicts.Count > 0;
}
