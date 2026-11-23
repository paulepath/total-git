namespace TotalGit.Core.Git;

/// <summary>
/// Lines up the removed and added lines of one change block for the side-by-side view. Lines that are alike sit on
/// the same row (a method's old signature beside its new one, wherever in the block that is); the lines between
/// them fill rows from the top, as a block with nothing alike does throughout.
/// </summary>
public static class ChangeAlignment
{
    /// <summary>Lines less alike than this aren't lined up with each other.</summary>
    private const double MinSimilarity = 0.5;

    /// <summary>What two identical lines with no words in them (blank, a lone brace) count for: little, but not nothing.</summary>
    private const double PlainMatch = 0.35;

    /// <summary>
    /// The cost of a stretch of rows with one side empty. It keeps each side's lines together: a line is moved
    /// away from its neighbours only for a line that much more like it than the one it would sit beside anyway.
    /// </summary>
    private const double BreakCost = 0.3;

    /// <summary>Past this many removed × added lines the block is paired from the top, as it's too big to compare.</summary>
    private const int MaxCells = 100_000;

    private const int MaxWords = 200;

    /// <summary>
    /// The block's rows, as indexes into <paramref name="removed"/> and <paramref name="added"/> (-1: nothing on
    /// that side). Each side's lines stay in order.
    /// </summary>
    public static List<(int Removed, int Added)> Rows(IReadOnlyList<DiffLine> removed, IReadOnlyList<DiffLine> added)
    {
        var rows = new List<(int, int)>(Math.Max(removed.Count, added.Count));
        int r = 0, a = 0;
        foreach (var (ri, ai) in Alike(removed, added))
        {
            Fill(ri, ai);
            rows.Add((r++, a++));
        }
        Fill(removed.Count, added.Count);
        return rows;

        // The lines before the next pair, from the top.
        void Fill(int removedEnd, int addedEnd)
        {
            while (r < removedEnd || a < addedEnd)
                rows.Add((r < removedEnd ? r++ : -1, a < addedEnd ? a++ : -1));
        }
    }

    /// <summary>
    /// The pairs of lines to put on the same row, in order: the choice that scores highest, where a pair scores how
    /// alike its lines are and each stretch of rows with one side empty costs <see cref="BreakCost"/>.
    /// </summary>
    private static List<(int Removed, int Added)> Alike(IReadOnlyList<DiffLine> removed, IReadOnlyList<DiffLine> added)
    {
        int rn = removed.Count, an = added.Count;
        if (rn == 0 || an == 0 || (long)rn * an > MaxCells) return [];

        var left = removed.Select(Words.Of).ToArray();
        var right = added.Select(Words.Of).ToArray();

        // best: the highest score of a run of pairs ending with (i, j). upTo: the highest of those at or before
        // (i, j) on both sides. Both with where the run came from, to walk back along it.
        var best = new double[rn * an];
        var from = new int[rn * an];
        var upTo = new double[rn * an];
        var upToAt = new int[rn * an];
        // The highest so far on each diagonal: a pair further down it follows with no empty rows between.
        var diagonal = new double[rn + an];
        var diagonalAt = new int[rn + an];
        Array.Fill(diagonal, double.NegativeInfinity);

        for (var i = 0; i < rn; i++)
        {
            for (var j = 0; j < an; j++)
            {
                var cell = i * an + j;
                best[cell] = double.NegativeInfinity;
                from[cell] = -1;
                var alike = Words.Similarity(left[i], right[j]);
                if (alike > 0)
                {
                    // The first pair: the lines above it leave rows with one side empty unless there are as many each side.
                    var score = i == j ? 0 : -BreakCost;
                    var d = i - j + an;
                    if (diagonal[d] > score) (score, from[cell]) = (diagonal[d], diagonalAt[d]);
                    if (i > 0 && j > 0 && upTo[cell - an - 1] - BreakCost > score)
                        (score, from[cell]) = (upTo[cell - an - 1] - BreakCost, upToAt[cell - an - 1]);
                    best[cell] = score + alike;
                    if (best[cell] > diagonal[d]) (diagonal[d], diagonalAt[d]) = (best[cell], cell);
                }

                (upTo[cell], upToAt[cell]) = (best[cell], cell);
                if (i > 0 && upTo[cell - an] >= upTo[cell]) (upTo[cell], upToAt[cell]) = (upTo[cell - an], upToAt[cell - an]);
                if (j > 0 && upTo[cell - 1] >= upTo[cell]) (upTo[cell], upToAt[cell]) = (upTo[cell - 1], upToAt[cell - 1]);
            }
        }

        // No pairs at all is the score to beat.
        var top = rn == an ? 0 : -BreakCost;
        var last = -1;
        for (var cell = 0; cell < best.Length; cell++)
        {
            int i = cell / an, j = cell % an;
            var total = best[cell] - (rn - i == an - j ? 0 : BreakCost);
            if (total > top + 1e-9) (top, last) = (total, cell);
        }

        var pairs = new List<(int, int)>();
        for (var cell = last; cell >= 0; cell = from[cell]) pairs.Add((cell / an, cell % an));
        pairs.Reverse();
        return pairs;
    }

    /// <summary>A line's words, to compare it with another line by.</summary>
    private readonly record struct Words(int[] Hashes, string? Plain)
    {
        /// <summary>A line that's never lined up with another (a "\ No newline" marker).</summary>
        private static readonly Words None = new([], null);

        public static Words Of(DiffLine line)
        {
            if (line.Kind is not (DiffLineKind.Removed or DiffLineKind.Added)) return None;
            var text = line.Text.AsSpan();
            var hashes = new List<int>();
            for (var i = 0; i < text.Length && hashes.Count < MaxWords;)
            {
                if (!IsWord(text[i]))
                {
                    i++;
                    continue;
                }
                var start = i;
                while (i < text.Length && IsWord(text[i])) i++;
                hashes.Add(string.GetHashCode(text[start..i]));
            }
            hashes.Sort();
            return new Words([.. hashes], hashes.Count == 0 ? line.Text.Trim() : null);
        }

        /// <summary>
        /// How alike two lines are, from 0 to 1: the share of their words they have in common, whatever the order
        /// or the indentation. 0 when it's under <see cref="MinSimilarity"/>.
        /// </summary>
        public static double Similarity(Words a, Words b)
        {
            if (a.Plain is not null || b.Plain is not null) return a.Plain is not null && a.Plain == b.Plain ? PlainMatch : 0;
            if (a.Hashes.Length == 0 || b.Hashes.Length == 0) return 0;
            var common = 0;
            for (int i = 0, j = 0; i < a.Hashes.Length && j < b.Hashes.Length;)
            {
                if (a.Hashes[i] == b.Hashes[j])
                {
                    common++;
                    i++;
                    j++;
                }
                else if (a.Hashes[i] < b.Hashes[j]) i++;
                else j++;
            }
            var alike = 2.0 * common / (a.Hashes.Length + b.Hashes.Length);
            return alike >= MinSimilarity ? alike : 0;
        }

        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
