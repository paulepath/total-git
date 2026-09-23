using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.Core.Git;

namespace TotalGit.App.ViewModels;

/// <summary>One line of the ours/theirs panes.</summary>
/// <param name="Conflict">Index of the conflict this line belongs to, or -1 for shared text.</param>
public sealed record MergeLine(int Number, string Text, int Conflict, bool IsCurrent)
{
    public bool IsConflict => Conflict >= 0 && !IsCurrent;
}

/// <summary>
/// The 3-pane merge tool for one conflicted file: ours and theirs on top, the editable result
/// below. Each conflict is resolved by picking a side (or both); the result can also be edited by hand.
/// </summary>
public sealed partial class MergeToolViewModel : ObservableObject
{
    private readonly string _worktree;
    private readonly Func<string, Task<bool>> _markResolved;
    private readonly Func<string, bool, Task> _takeWholeFile;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action _close;
    private readonly ConflictFile? _file;
    private readonly ConflictChoice[] _choices = [];
    private readonly bool _hasBom;
    private bool _settingResult;

    public MergeToolViewModel(
        string worktree,
        string path,
        Func<string, Task<bool>> markResolved,
        Func<string, bool, Task> takeWholeFile,
        Func<string, string, Task<bool>> confirm,
        Action close)
    {
        _worktree = worktree;
        Path = path;
        _markResolved = markResolved;
        _takeWholeFile = takeWholeFile;
        _confirm = confirm;
        _close = close;

        var bytes = File.ReadAllBytes(FullPath);
        IsBinary = bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0);
        if (IsBinary) return;

        _hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        _file = ConflictFile.Parse(new UTF8Encoding(false).GetString(bytes, _hasBom ? 3 : 0, bytes.Length - (_hasBom ? 3 : 0)));
        _choices = new ConflictChoice[_file.Conflicts.Count];
        CurrentIndex = 0;
        RenderResult();
        RebuildSides();
    }

    public string Path { get; }
    private string FullPath => System.IO.Path.Combine(_worktree, Path);
    public bool IsBinary { get; }
    public bool IsText => !IsBinary;

    public string OursLabel => _file?.OursLabel is { Length: > 0 } l ? l : "ours";
    public string TheirsLabel => _file?.TheirsLabel is { Length: > 0 } l ? l : "theirs";

    public int ConflictCount => _choices.Length;
    public bool HasConflicts => ConflictCount > 0;

    public ObservableCollection<MergeLine> OursLines { get; } = [];
    public ObservableCollection<MergeLine> TheirsLines { get; } = [];

    /// <summary>Raised with the line to scroll both panes to when the current conflict changes.</summary>
    public event Action<int, int>? ScrollRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText), nameof(CurrentChoiceText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(NextCommand))]
    public partial int CurrentIndex { get; set; }

    public string PositionText => ConflictCount == 0 ? "No conflict markers" : $"Conflict {CurrentIndex + 1} of {ConflictCount}";

    public string UnresolvedText
    {
        get
        {
            var left = _choices.Count(c => c == ConflictChoice.Unresolved);
            return IsManual ? "Edited by hand" : left == 0 ? "All conflicts resolved" : $"{left} unresolved";
        }
    }

    public string CurrentChoiceText => ConflictCount == 0 ? "" : _choices[CurrentIndex] switch
    {
        ConflictChoice.Ours => $"Using {OursLabel}",
        ConflictChoice.Theirs => $"Using {TheirsLabel}",
        ConflictChoice.OursThenTheirs => $"Using both ({OursLabel} first)",
        ConflictChoice.TheirsThenOurs => $"Using both ({TheirsLabel} first)",
        _ => "Not resolved yet",
    };

    [ObservableProperty]
    public partial string Result { get; set; } = "";

    /// <summary>The result was typed into, so picking sides no longer rewrites it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnresolvedText))]
    public partial bool IsManual { get; set; }

    partial void OnResultChanged(string value)
    {
        if (!_settingResult) IsManual = true;
    }

    partial void OnCurrentIndexChanged(int value) => RebuildSides();

    private void RenderResult()
    {
        if (_file is null) return;
        _settingResult = true;
        Result = _file.Render(_choices);
        _settingResult = false;
        OnPropertyChanged(nameof(UnresolvedText));
        OnPropertyChanged(nameof(CurrentChoiceText));
    }

    private void RebuildSides()
    {
        if (_file is null) return;
        Fill(OursLines, _file.Side(ours: true));
        Fill(TheirsLines, _file.Side(ours: false));
        var ours = _file.Side(true).Conflicts;
        var theirs = _file.Side(false).Conflicts;
        if (CurrentIndex < ours.Count) ScrollRequested?.Invoke(ours[CurrentIndex].Start, theirs[CurrentIndex].Start);
    }

    private void Fill(ObservableCollection<MergeLine> target, ConflictSide side)
    {
        var owner = new int[side.Lines.Count];
        Array.Fill(owner, -1);
        for (var c = 0; c < side.Conflicts.Count; c++)
            for (var i = 0; i < side.Conflicts[c].Count; i++) owner[side.Conflicts[c].Start + i] = c;

        target.Clear();
        for (var i = 0; i < side.Lines.Count; i++)
            target.Add(new MergeLine(i + 1, side.Lines[i], owner[i], owner[i] >= 0 && owner[i] == CurrentIndex));

        // An empty side of a conflict still needs a visible row to show where it is.
        for (var c = side.Conflicts.Count - 1; c >= 0; c--)
            if (side.Conflicts[c].Count == 0)
                target.Insert(side.Conflicts[c].Start, new MergeLine(0, "(nothing)", c, c == CurrentIndex));
    }

    private bool CanPrevious() => CurrentIndex > 0;
    private bool CanNext() => CurrentIndex < ConflictCount - 1;

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void Previous() => CurrentIndex--;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next() => CurrentIndex++;

    [RelayCommand]
    private void Choose(ConflictChoice choice)
    {
        if (ConflictCount == 0) return;
        _choices[CurrentIndex] = choice;
        IsManual = false;
        RenderResult();
        // Move on to the next unresolved conflict, like most merge tools.
        var next = Array.FindIndex(_choices, CurrentIndex + 1, c => c == ConflictChoice.Unresolved);
        if (next >= 0) CurrentIndex = next;
    }

    /// <summary>Discards hand edits and rebuilds the result from the choices.</summary>
    [RelayCommand]
    private void ResetResult()
    {
        IsManual = false;
        RenderResult();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_file is null) return;
        if (ConflictFile.HasMarkers(Result)
            && !await _confirm("Conflict markers left", "The result still contains conflict markers. Save it and mark the file resolved anyway?"))
            return;

        var text = Result;
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
