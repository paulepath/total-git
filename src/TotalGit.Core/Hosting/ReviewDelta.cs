using TotalGit.Core.Git;

namespace TotalGit.Core.Hosting;

/// <summary>Where a file changed since it was reviewed, in the current version's line numbers.</summary>
/// <param name="NewLines">Lines added or rewritten since the review.</param>
/// <param name="RemovedBefore">Lines just above which something was deleted since the review (the line after the gap).</param>
public sealed record ReviewDelta(IReadOnlySet<int> NewLines, IReadOnlySet<int> RemovedBefore)
{
    public static ReviewDelta None { get; } = new(new HashSet<int>(), new HashSet<int>());

    public int Count => NewLines.Count + RemovedBefore.Count(l => !NewLines.Contains(l));

    /// <summary>From the diff of the file between the reviewed commit and the current head.</summary>
    public static ReviewDelta From(FileDiff sinceReview)
    {
        var added = new HashSet<int>();
        var removed = new HashSet<int>();
        // The next line of the new version, so a removal can be placed above it.
        var nextNew = 1;
        foreach (var line in sinceReview.Lines)
        {
            switch (line.Kind)
            {
                case DiffLineKind.Hunk:
                    nextNew = HunkNewStart(line.Text) ?? nextNew;
                    break;
                case DiffLineKind.Added when line.NewLine is { } n:
                    added.Add(n);
                    nextNew = n + 1;
                    break;
                case DiffLineKind.Context when line.NewLine is { } n:
                    nextNew = n + 1;
                    break;
                case DiffLineKind.Removed:
                    removed.Add(nextNew);
                    break;
            }
        }
        return new ReviewDelta(added, removed);
    }

    /// <summary>The new-side start of a hunk header, <c>@@ -a,b +c,d @@</c>.</summary>
    private static int? HunkNewStart(string header)
    {
        var plus = header.IndexOf('+');
        if (plus < 0) return null;
        var end = plus + 1;
        while (end < header.Length && char.IsAsciiDigit(header[end])) end++;
        return int.TryParse(header.AsSpan(plus + 1, end - plus - 1), out var start) ? Math.Max(1, start) : null;
    }
}
