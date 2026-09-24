namespace TotalGit.Core.Git;

/// <summary>A changed part of a line's text: <paramref name="Start"/> and <paramref name="Length"/> in characters.</summary>
public readonly record struct TextRange(int Start, int Length);

/// <summary>The changed parts of each removed or added line, from <see cref="IntraLineDiff.Compute"/>.</summary>
public sealed class IntraLineHighlights
{
    public static IntraLineHighlights None { get; } = new(new Dictionary<(bool, int), IReadOnlyList<TextRange>>());

    private readonly Dictionary<(bool Added, int Line), IReadOnlyList<TextRange>> _ranges;

    internal IntraLineHighlights(Dictionary<(bool, int), IReadOnlyList<TextRange>> ranges) => _ranges = ranges;

    /// <summary>The changed ranges of a removed or added line (empty when the whole line is just tinted).</summary>
    public IReadOnlyList<TextRange> For(DiffLine line) => Key(line) is { } key && _ranges.TryGetValue(key, out var r) ? r : [];

    internal static (bool, int)? Key(DiffLine line) => line.Kind switch
    {
        DiffLineKind.Removed when line.OldLine is { } o => (false, o),
        DiffLineKind.Added when line.NewLine is { } n => (true, n),
        _ => null,
    };
}

/// <summary>
/// Finds which words changed between a removed line and the added line that replaced it, so the diff view can
/// highlight them more strongly than the rest of the line.
/// </summary>
public static class IntraLineDiff
{
    private const int MaxTokens = 400;
    private const double MinSimilarity = 0.3;

    public static IntraLineHighlights Compute(IReadOnlyList<DiffLine> lines)
    {
        var result = new Dictionary<(bool, int), IReadOnlyList<TextRange>>();
        var removed = new List<DiffLine>();
        var added = new List<DiffLine>();

        void Flush()
        {
            // Pair lines in order, as the side-by-side view shows them.
            for (var i = 0; i < Math.Min(removed.Count, added.Count); i++)
            {
                if (Compare(removed[i].Text, added[i].Text) is not var (oldRanges, newRanges)) continue;
                if (oldRanges.Count > 0) result[IntraLineHighlights.Key(removed[i])!.Value] = oldRanges;
                if (newRanges.Count > 0) result[IntraLineHighlights.Key(added[i])!.Value] = newRanges;
            }
            removed.Clear();
            added.Clear();
        }

        foreach (var line in lines)
        {
            switch (line.Kind)
            {
                case DiffLineKind.Removed:
                    if (added.Count > 0) Flush();
                    removed.Add(line);
                    break;
                case DiffLineKind.Added:
                    added.Add(line);
                    break;
                case DiffLineKind.NoNewline:
                    break;
                default:
                    Flush();
                    break;
            }
        }
        Flush();
        return result.Count == 0 ? IntraLineHighlights.None : new IntraLineHighlights(result);
    }

    /// <summary>
    /// The changed ranges in each line, or null when the lines are too different (or too long) for word
    /// highlights to help.
    /// </summary>
    public static (List<TextRange> Old, List<TextRange> New)? Compare(string oldText, string newText)
    {
        var a = Tokenize(oldText);
        var b = Tokenize(newText);
        if (a.Count > MaxTokens || b.Count > MaxTokens) return null;

        // Longest common subsequence of tokens.
        var n = a.Count;
        var m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = Same(oldText, a[i], newText, b[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var matchedA = new bool[n];
        var matchedB = new bool[m];
        var common = 0;
        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (Same(oldText, a[i], newText, b[j]))
            {
                matchedA[i] = matchedB[j] = true;
                common += NonSpace(oldText, a[i]);
                i++;
                j++;
            }
            else if (lcs[i + 1, j] >= lcs[i, j + 1]) i++;
            else j++;
        }

        var total = NonSpace(oldText, new TextRange(0, oldText.Length)) + NonSpace(newText, new TextRange(0, newText.Length));
        if (total > 0 && 2.0 * common / total < MinSimilarity) return null;

        return (Unmatched(a, matchedA), Unmatched(b, matchedB));
    }

    private static List<TextRange> Unmatched(List<TextRange> tokens, bool[] matched)
    {
        var ranges = new List<TextRange>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (matched[i]) continue;
            var t = tokens[i];
            // Neighbouring changed tokens become one range.
            if (ranges.Count > 0 && ranges[^1].Start + ranges[^1].Length == t.Start)
                ranges[^1] = ranges[^1] with { Length = ranges[^1].Length + t.Length };
            else
                ranges.Add(t);
        }
        return ranges;
    }

    /// <summary>Words (letters, digits, _), runs of whitespace, and single punctuation characters.</summary>
    private static List<TextRange> Tokenize(string text)
    {
        var tokens = new List<TextRange>();
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var c = text[i];
            if (IsWord(c)) while (i < text.Length && IsWord(text[i])) i++;
            else if (char.IsWhiteSpace(c)) while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            else i++;
            tokens.Add(new TextRange(start, i - start));
        }
        return tokens;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool Same(string a, TextRange x, string b, TextRange y) =>
        x.Length == y.Length && a.AsSpan(x.Start, x.Length).SequenceEqual(b.AsSpan(y.Start, y.Length));

    private static int NonSpace(string text, TextRange r)
    {
        var count = 0;
        foreach (var c in text.AsSpan(r.Start, r.Length))
            if (!char.IsWhiteSpace(c)) count++;
        return count;
    }
}
