namespace TotalGit.Core.Git;

/// <summary>One row of a side-by-side diff. A null side is a filler; hunk rows span both sides.</summary>
public readonly record struct SplitRow(DiffLine? Left, DiffLine? Right, bool IsHunk);

/// <summary>Turns parsed unified diff lines into aligned side-by-side rows.</summary>
public static class SplitDiff
{
    public static IReadOnlyList<SplitRow> Build(IReadOnlyList<DiffLine> lines)
    {
        var rows = new List<SplitRow>(lines.Count);
        var removed = new List<DiffLine>();
        var added = new List<DiffLine>();
        // Which side the last line went to, so a "\ No newline" marker follows it.
        var lastSide = 0; // -1 left, 1 right, 0 both

        void Flush()
        {
            var n = Math.Max(removed.Count, added.Count);
            for (var i = 0; i < n; i++)
                rows.Add(new SplitRow(i < removed.Count ? removed[i] : null, i < added.Count ? added[i] : null, false));
            removed.Clear();
            added.Clear();
        }

        foreach (var line in lines)
        {
            switch (line.Kind)
            {
                case DiffLineKind.Removed:
                    // A removal after additions starts a new change block.
                    if (added.Count > 0) Flush();
                    removed.Add(line);
                    lastSide = -1;
                    break;
                case DiffLineKind.Added:
                    added.Add(line);
                    lastSide = 1;
                    break;
                case DiffLineKind.NoNewline:
                    if (lastSide == -1) removed.Add(line);
                    else if (lastSide == 1) added.Add(line);
                    else
                    {
                        Flush();
                        rows.Add(new SplitRow(line, line, false));
                    }
                    break;
                case DiffLineKind.Hunk:
                    Flush();
                    rows.Add(new SplitRow(line, line, true));
                    lastSide = 0;
                    break;
                default:
                    Flush();
                    rows.Add(new SplitRow(line, line, false));
                    lastSide = 0;
                    break;
            }
        }
        Flush();
        return rows;
    }
}
