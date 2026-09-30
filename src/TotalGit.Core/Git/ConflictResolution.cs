namespace TotalGit.Core.Git;

/// <summary>A version of a conflicted file: the common ancestor, ours (HEAD) or theirs.</summary>
public enum MergeSide
{
    Base,
    Ours,
    Theirs,
}

/// <summary>Where a line of the merge result came from.</summary>
public enum ResultSource
{
    Common,
    Base,
    Ours,
    Theirs,
    /// <summary>Typed by hand.</summary>
    Custom,
}

/// <param name="SourceLine">The line's index in its side of the conflict, or -1 (common or typed text).</param>
public readonly record struct ResultLine(string Text, ResultSource Source, int SourceLine = -1);

/// <summary>One side picked for a conflict, with the lines of it that are kept.</summary>
public sealed record ConflictPick(MergeSide Side, IReadOnlySet<int> Lines);

/// <summary>
/// What goes into the result for one conflict, kdiff3 style: sides are added in the order they are picked
/// (pick ours then theirs and the result is ours' lines then theirs'), single lines can be left out, and the
/// block can be typed by hand.
/// </summary>
public sealed class ConflictResolution
{
    private readonly List<(MergeSide Side, SortedSet<int> Lines)> _picks = [];

    public int PickCount => _picks.Count;

    public IReadOnlyList<ConflictPick> Picks => _picks.Select(p => new ConflictPick(p.Side, p.Lines)).ToList();

    /// <summary>Lines typed by hand; they replace the picks while set.</summary>
    public IReadOnlyList<string>? CustomLines { get; private set; }

    /// <summary>Deliberately resolved to nothing (which is not the same as not resolved yet).</summary>
    public bool IsEmpty { get; private set; }

    public bool IsResolved => CustomLines is not null || IsEmpty || _picks.Count > 0;

    public bool IsCustom => CustomLines is not null;

    /// <summary>1-based place of a side in the pick order, or 0 when it isn't picked.</summary>
    public int OrderOf(MergeSide side) => _picks.FindIndex(p => p.Side == side) + 1;

    /// <summary>True when the whole side is picked (not just some of its lines).</summary>
    public bool HasAll(MergeSide side, int count) => _picks.Find(p => p.Side == side).Lines is { } lines && lines.Count == count;

    public bool Has(MergeSide side, int line) => _picks.Find(p => p.Side == side).Lines?.Contains(line) == true;

    /// <summary>Adds all of a side after what is already picked, or takes it out when it is all there already.</summary>
    public void ToggleSide(MergeSide side, int count)
    {
        CustomLines = null;
        IsEmpty = false;
        var index = _picks.FindIndex(p => p.Side == side);
        if (index >= 0 && _picks[index].Lines.Count == count)
        {
            _picks.RemoveAt(index);
            return;
        }
        var all = new SortedSet<int>(Enumerable.Range(0, count));
        if (index >= 0) _picks[index] = (side, all);
        else _picks.Add((side, all));
    }

    /// <summary>Adds or removes one line of a side (the side joins the end of the order if it wasn't picked).</summary>
    public void ToggleLine(MergeSide side, int line)
    {
        CustomLines = null;
        IsEmpty = false;
        var index = _picks.FindIndex(p => p.Side == side);
        if (index < 0)
        {
            _picks.Add((side, [line]));
            return;
        }
        var lines = _picks[index].Lines;
        if (!lines.Remove(line)) lines.Add(line);
        else if (lines.Count == 0) _picks.RemoveAt(index);
    }

    /// <summary>Replaces the picks with whole sides, in this order.</summary>
    public void Set(ConflictHunk hunk, params MergeSide[] sides)
    {
        Clear();
        foreach (var side in sides) ToggleSide(side, SideLines(hunk, side).Count);
    }

    public void UseNothing()
    {
        Clear();
        IsEmpty = true;
    }

    public void SetCustom(IReadOnlyList<string> lines) => CustomLines = lines;

    public void Clear()
    {
        _picks.Clear();
        CustomLines = null;
        IsEmpty = false;
    }

    public void SwapOrder() => _picks.Reverse();

    /// <summary>The block's lines in the result (empty while unresolved).</summary>
    public IReadOnlyList<ResultLine> Lines(ConflictHunk hunk)
    {
        if (CustomLines is not null) return CustomLines.Select(l => new ResultLine(l, ResultSource.Custom)).ToList();
        var result = new List<ResultLine>();
        foreach (var (side, lines) in _picks)
        {
            var text = SideLines(hunk, side);
            var source = side switch { MergeSide.Base => ResultSource.Base, MergeSide.Ours => ResultSource.Ours, _ => ResultSource.Theirs };
            foreach (var i in lines)
                if (i < text.Count) result.Add(new ResultLine(text[i], source, i));
        }
        return result;
    }

    public static IReadOnlyList<string> SideLines(ConflictHunk hunk, MergeSide side) => side switch
    {
        MergeSide.Base => hunk.Base ?? [],
        MergeSide.Ours => hunk.Ours,
        _ => hunk.Theirs,
    };
}

/// <summary>A conflicted file being merged: how each conflict is resolved, plus any hand edits to the shared text.</summary>
public sealed class MergeDocument
{
    private readonly Dictionary<int, IReadOnlyList<string>> _commonEdits = [];

    public MergeDocument(ConflictFile file)
    {
        File = file;
        Resolutions = file.Conflicts.Select(_ => new ConflictResolution()).ToArray();
    }

    public ConflictFile File { get; }
    public IReadOnlyList<ConflictResolution> Resolutions { get; }

    /// <summary>Every conflict has the common ancestor's text (diff3-style markers).</summary>
    public bool HasBase => File.Conflicts.Count > 0 && File.Conflicts.All(c => c.Base is not null);

    public int UnresolvedCount => Resolutions.Count(r => !r.IsResolved);

    /// <summary>Hand-edited text replacing a shared segment (by index in <see cref="ConflictFile.Segments"/>).</summary>
    public IReadOnlyList<string>? CommonEdit(int segment) => _commonEdits.GetValueOrDefault(segment);

    public void SetCommonEdit(int segment, IReadOnlyList<string>? lines)
    {
        if (lines is null) _commonEdits.Remove(segment);
        else _commonEdits[segment] = lines;
    }

    public bool HasCommonEdits => _commonEdits.Count > 0;

    public void ClearCommonEdits() => _commonEdits.Clear();

    /// <summary>The file to save: resolved conflicts replaced, unresolved ones keeping their markers.</summary>
    public string Render()
    {
        var lines = new List<string>();
        var n = 0;
        for (var s = 0; s < File.Segments.Count; s++)
        {
            switch (File.Segments[s])
            {
                case CommonSegment c:
                    lines.AddRange(CommonEdit(s) ?? c.Lines);
                    break;
                case ConflictHunk hunk:
                    var resolution = Resolutions[n++];
                    if (resolution.IsResolved) lines.AddRange(resolution.Lines(hunk).Select(l => l.Text));
                    else ConflictFile.AppendMarkers(lines, hunk);
                    break;
            }
        }
        return File.Join(lines);
    }
}
