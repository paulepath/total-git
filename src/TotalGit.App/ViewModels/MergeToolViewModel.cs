using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Git;

namespace TotalGit.App.ViewModels;

/// <summary>
/// The merge tool for one conflicted file, kdiff3 style: base, ours and theirs side by side over the result, all on
/// one grid of rows so they scroll together. Each conflict is resolved by picking sides (in order) or single lines,
/// or by typing; the whole result can also be edited as plain text.
/// </summary>
public sealed partial class MergeToolViewModel : ObservableObject
{
    private readonly string _worktree;
    private readonly AppSettings _settings;
    private readonly Func<string, Task<bool>> _markResolved;
    private readonly Func<string, bool, Task> _takeWholeFile;
    private readonly Func<string, string, string, Task<bool>> _confirm;
    private readonly Action _close;
    private readonly ConflictFile? _file;
    private readonly bool _hasBom;
    private readonly ConflictSides? _sides;
    private bool _settingResult;

    /// <summary>Reads the file (and, when git can give it, the common ancestor of each conflict).</summary>
    public static async Task<MergeToolViewModel> LoadAsync(
        string worktree,
        string path,
        AppSettings settings,
        Func<string, Task<bool>> markResolved,
        Func<string, bool, Task> takeWholeFile,
        Func<string, string, string, Task<bool>> confirm,
        Action close)
    {
        var bytes = await File.ReadAllBytesAsync(System.IO.Path.Combine(worktree, path));
        string? text = null;
        var hasBom = false;
        if (!bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
        {
            hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
            var parsed = ConflictFile.Parse(text);
            if (parsed.Conflicts.Count > 0 && parsed.Conflicts.Any(c => c.Base is null))
            {
                try
                {
                    text = await GitActions.ConflictWithBaseAsync(worktree, path, text, parsed.OursLabel, parsed.TheirsLabel) ?? text;
                }
                catch (Exception ex) when (ex is GitCommandException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // No base pane, then.
                }
            }
        }
        ConflictSides? sides = null;
        try { sides = await ConflictSides.DescribeAsync(worktree); }
        catch (Exception ex) when (ex is GitCommandException or IOException or System.ComponentModel.Win32Exception)
        {
            // git's own labels, then.
        }
        return new MergeToolViewModel(worktree, path, text, hasBom, sides, settings, markResolved, takeWholeFile, confirm, close);
    }

    private MergeToolViewModel(
        string worktree,
        string path,
        string? text,
        bool hasBom,
        ConflictSides? sides,
        AppSettings settings,
        Func<string, Task<bool>> markResolved,
        Func<string, bool, Task> takeWholeFile,
        Func<string, string, string, Task<bool>> confirm,
        Action close)
    {
        _worktree = worktree;
        Path = path;
        _settings = settings;
        _markResolved = markResolved;
        _takeWholeFile = takeWholeFile;
        _confirm = confirm;
        _close = close;
        _hasBom = hasBom;
        _sides = sides;
        IsBinary = text is null;
        if (text is null) return;

        _file = ConflictFile.Parse(text);
        Document = new MergeDocument(_file);
        Layout = MergeLayout.Build(Document);
    }

    public string Path { get; }
    private string FullPath => System.IO.Path.Combine(_worktree, Path);
    public bool IsBinary { get; }
    public bool IsText => !IsBinary;

    public MergeDocument? Document { get; }

    /// <summary>Rebuilt whenever a pick or an edit changes the result.</summary>
    [ObservableProperty]
    public partial MergeLayout? Layout { get; private set; }

    // git's marker labels ("HEAD", a commit) unless the operation says better what each side is.
    public string OursLabel => _sides?.Ours ?? (_file?.OursLabel is { Length: > 0 } l ? l : "ours");
    public string TheirsLabel => _sides?.Theirs ?? (_file?.TheirsLabel is { Length: > 0 } l ? l : "theirs");

    /// <summary>What each side is (e.g. "where you're rebasing to"); empty when there's no operation to say.</summary>
    public string OursRole => _sides?.OursRole ?? "";
    public string TheirsRole => _sides?.TheirsRole ?? "";
    public string BaseLabel => "Base (common ancestor)";

    public int ConflictCount => _file?.Conflicts.Count ?? 0;
    public bool HasConflicts => ConflictCount > 0;
    public bool HasBase => Document?.HasBase == true;

    /// <summary>The base pane is shown (when there is a base).</summary>
    public bool ShowBase
    {
        get => HasBase && _settings.MergeShowBase;
        set
        {
            if (!HasBase || value == _settings.MergeShowBase) return;
            _settings.MergeShowBase = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Raised with the conflict to bring into view.</summary>
    public event Action<int>? RevealConflict;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(NextCommand))]
    public partial int CurrentIndex { get; set; }

    partial void OnCurrentIndexChanged(int value)
    {
        NotifyPicks();
        RevealConflict?.Invoke(value);
    }

    public string PositionText => ConflictCount == 0 ? "No conflict markers" : $"Conflict {CurrentIndex + 1} of {ConflictCount}";

    public string UnresolvedText
    {
        get
        {
            if (IsTextMode && IsManual) return "Edited as text";
            var left = Document?.UnresolvedCount ?? 0;
            return left == 0 ? "All conflicts resolved" : $"{left} unresolved";
        }
    }

    private ConflictResolution? Current => Document is { } d && CurrentIndex < d.Resolutions.Count ? d.Resolutions[CurrentIndex] : null;
    private ConflictHunk? CurrentHunk => _file is { } f && CurrentIndex < f.Conflicts.Count ? f.Conflicts[CurrentIndex] : null;

    public bool IsBasePicked => Current?.OrderOf(MergeSide.Base) > 0;
    public bool IsOursPicked => Current?.OrderOf(MergeSide.Ours) > 0;
    public bool IsTheirsPicked => Current?.OrderOf(MergeSide.Theirs) > 0;

    /// <summary>What the current conflict resolves to, in words.</summary>
    public string CurrentChoiceText
    {
        get
        {
            if (Current is not { } r || CurrentHunk is not { } hunk) return "";
            if (r.IsCustom) return "Typed by hand";
            if (r.IsEmpty) return "Using nothing";
            if (!r.IsResolved) return "Not resolved yet";
            var parts = r.Picks.Select(p =>
            {
                var count = ConflictResolution.SideLines(hunk, p.Side).Count;
                var name = SideName(p.Side);
                return p.Lines.Count == count ? name : $"{p.Lines.Count} of {count} lines of {name}";
            });
            return "Using " + string.Join(", then ", parts);
        }
    }

    public string SideName(MergeSide side) => side switch
    {
        MergeSide.Base => "base",
        MergeSide.Ours => OursLabel,
        _ => TheirsLabel,
    };

    // ------------------------------------------------------------------ picking

    /// <summary>Adds a whole side to the current conflict (after what's picked), or takes it out again.</summary>
    [RelayCommand]
    private void ToggleSide(MergeSide side)
    {
        if (Current is not { } r || CurrentHunk is not { } hunk) return;
        if (side == MergeSide.Base && !HasBase) return;
        r.ToggleSide(side, ConflictResolution.SideLines(hunk, side).Count);
        Changed();
    }

    /// <summary>Picks or drops one line of a side in a conflict (and makes that conflict the current one).</summary>
    public void ToggleConflictLine(int conflict, MergeSide side, int line)
    {
        if (Document is null || conflict < 0 || conflict >= ConflictCount) return;
        CurrentIndex = conflict;
        Document.Resolutions[conflict].ToggleLine(side, line);
        Changed();
    }

    /// <summary>Picks or drops a whole side of a conflict (and makes that conflict the current one).</summary>
    public void ToggleConflictSide(int conflict, MergeSide side)
    {
        if (conflict < 0 || conflict >= ConflictCount) return;
        CurrentIndex = conflict;
        ToggleSide(side);
    }

    [RelayCommand]
    private void TakeBoth(bool oursFirst)
    {
        if (Current is not { } r || CurrentHunk is not { } hunk) return;
        r.Set(hunk, oursFirst ? [MergeSide.Ours, MergeSide.Theirs] : [MergeSide.Theirs, MergeSide.Ours]);
        Changed();
    }

    [RelayCommand]
    private void UseNothing()
    {
        Current?.UseNothing();
        Changed();
    }

    [RelayCommand]
    private void SwapOrder()
    {
        Current?.SwapOrder();
        Changed();
    }

    [RelayCommand]
    private void ClearChoice()
    {
        Current?.Clear();
        Changed();
    }

    /// <summary>Replaces a block of the result with typed text: a conflict, or a run of shared lines.</summary>
    public void EditSegment(int segment, IReadOnlyList<string> lines)
    {
        if (Document is null || _file is null || segment < 0 || segment >= _file.Segments.Count) return;
        if (_file.Segments[segment] is CommonSegment common)
        {
            Document.SetCommonEdit(segment, lines.SequenceEqual(common.Lines) ? null : lines);
        }
        else
        {
            var conflict = _file.Segments.Take(segment).OfType<ConflictHunk>().Count();
            Document.Resolutions[conflict].SetCustom(lines);
            CurrentIndex = conflict;
        }
        Changed();
    }

    /// <summary>The result's current lines for a segment (what the editor starts with).</summary>
    public IReadOnlyList<string> SegmentText(int segment)
    {
        if (Layout is not { } layout || segment < 0 || segment >= layout.SegmentRows.Count) return [];
        var (first, count) = layout.SegmentRows[segment];
        return layout.Rows.Skip(first).Take(count).Where(r => r.Result >= 0).Select(r => layout.ResultLines[r.Result].Text).ToList();
    }

    private void Changed()
    {
        if (Document is null) return;
        Layout = MergeLayout.Build(Document);
        NotifyPicks();
        OnPropertyChanged(nameof(UnresolvedText));
    }

    private void NotifyPicks()
    {
        OnPropertyChanged(nameof(IsBasePicked));
        OnPropertyChanged(nameof(IsOursPicked));
        OnPropertyChanged(nameof(IsTheirsPicked));
        OnPropertyChanged(nameof(CurrentChoiceText));
    }

    // ------------------------------------------------------------------ navigation

    private bool CanPrevious() => CurrentIndex > 0;
    private bool CanNext() => CurrentIndex < ConflictCount - 1;

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void Previous() => CurrentIndex--;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next() => CurrentIndex++;

    /// <summary>The next conflict still unresolved (wrapping round), if any.</summary>
    [RelayCommand]
    private void NextUnresolved()
    {
        if (Document is null) return;
        for (var step = 1; step <= ConflictCount; step++)
        {
            var i = (CurrentIndex + step) % ConflictCount;
            if (!Document.Resolutions[i].IsResolved)
            {
                CurrentIndex = i;
                return;
            }
        }
    }

    public void GoTo(int conflict)
    {
        if (conflict < 0 || conflict >= ConflictCount) return;
        if (conflict == CurrentIndex) RevealConflict?.Invoke(conflict);
        else CurrentIndex = conflict;
    }

    // ------------------------------------------------------------------ editing as text

    /// <summary>The whole result in a plain text box (not lined up with the other panes).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaneMode), nameof(UnresolvedText))]
    public partial bool IsTextMode { get; private set; }

    public bool IsPaneMode => !IsTextMode;

    [ObservableProperty]
    public partial string Result { get; set; } = "";

    /// <summary>The text box was typed into, so its text no longer follows the picks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnresolvedText))]
    public partial bool IsManual { get; set; }

    partial void OnResultChanged(string value)
    {
        if (!_settingResult) IsManual = true;
    }

    [RelayCommand]
    private async Task ToggleTextModeAsync()
    {
        if (Document is null) return;
        if (!IsTextMode)
        {
            _settingResult = true;
            Result = Document.Render();
            _settingResult = false;
            IsManual = false;
            IsTextMode = true;
            return;
        }
        if (IsManual && !await _confirm("Discard text edits?",
                "Going back to the side-by-side view throws away what you typed in the text box. Your picks are kept.", "Discard edits"))
            return;
        IsManual = false;
        IsTextMode = false;
    }

    // ------------------------------------------------------------------ saving

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_file is null || Document is null) return;
        var text = IsTextMode ? Result : Document.Render();
        if (ConflictFile.HasMarkers(text)
            && !await _confirm("Conflict markers left",
                "The result still contains conflict markers. Save it and mark the file resolved anyway?", "Save anyway"))
            return;

        // Keep the file's own line endings even if the text box normalised them.
        if (_file.NewLine == "\r\n") text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var bytes = new UTF8Encoding(false).GetBytes(text);
        await File.WriteAllBytesAsync(FullPath, _hasBom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes);
        await _markResolved(Path);
    }

    [RelayCommand]
    private Task TakeWholeFile(bool ours) => _takeWholeFile(Path, ours);

    [RelayCommand]
    private void Close() => _close();
}
