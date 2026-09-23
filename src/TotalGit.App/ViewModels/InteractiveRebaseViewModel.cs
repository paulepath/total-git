using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.Core.Git;

namespace TotalGit.App.ViewModels;

/// <summary>One commit in the interactive rebase list.</summary>
public sealed partial class RebaseRow : ObservableObject
{
    public RebaseRow(string sha, string subject, bool isPushed)
    {
        Sha = sha;
        Subject = subject;
        Message = subject;
        IsPushed = isPushed;
    }

    public static IReadOnlyList<RebaseAction> AllActions { get; } = Enum.GetValues<RebaseAction>();
    public IReadOnlyList<RebaseAction> Actions => AllActions;

    public string Sha { get; }
    public string ShortSha => Sha[..7];
    public string Subject { get; }
    public bool IsPushed { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReword), nameof(IsDropped), nameof(ActionHint))]
    public partial RebaseAction Action { get; set; }

    /// <summary>The new message when rewording (the first line is prefilled with the current subject).</summary>
    [ObservableProperty]
    public partial string Message { get; set; }

    public bool IsReword => Action == RebaseAction.Reword;
    public bool IsDropped => Action == RebaseAction.Drop;

    public string ActionHint => Action switch
    {
        RebaseAction.Pick => "Keep the commit as it is",
        RebaseAction.Reword => "Keep the commit, change its message",
        RebaseAction.Squash => "Combine into the commit above, keeping both messages",
        RebaseAction.Fixup => "Combine into the commit above, dropping this message",
        _ => "Remove the commit and its changes",
    };
}

/// <summary>State of the interactive rebase dialog: commits oldest first, each with an action.</summary>
public sealed partial class InteractiveRebaseViewModel : ObservableObject
{
    public InteractiveRebaseViewModel(string branch, string baseDescription, IEnumerable<RebaseRow> rows)
    {
        Branch = branch;
        BaseDescription = baseDescription;
        foreach (var row in rows)
        {
            row.PropertyChanged += (_, _) => Validate();
            Rows.Add(row);
        }
        Validate();
    }

    public string Branch { get; }
    public string BaseDescription { get; }
    public ObservableCollection<RebaseRow> Rows { get; } = [];

    public bool HasPushed => Rows.Any(r => r.IsPushed);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(HasError))]
    public partial string? Error { get; set; }

    public bool IsValid => Error is null;
    public bool HasError => Error is not null;

    private void Validate()
    {
        var kept = Rows.Where(r => !r.IsDropped).ToList();
        if (kept.FirstOrDefault() is { Action: RebaseAction.Squash or RebaseAction.Fixup })
            Error = "The first commit can't be squashed or fixed up: there's no commit above it to combine with.";
        else if (Rows.Any(r => r.IsReword && string.IsNullOrWhiteSpace(r.Message)))
            Error = "Enter a message for each reworded commit.";
        else
            Error = null;
    }

    [RelayCommand]
    private void MoveUp(RebaseRow row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(RebaseRow row) => Move(row, 1);

    private void Move(RebaseRow row, int delta)
    {
        var i = Rows.IndexOf(row);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Rows.Count) return;
        Rows.Move(i, j);
        Validate();
    }

    public IReadOnlyList<RebaseStep> Steps() =>
        Rows.Select(r => new RebaseStep(r.Action, r.Sha, r.IsReword ? r.Message.Trim() : null)).ToList();

    public bool IsUnchanged(IReadOnlyList<string> originalOrder) =>
        Rows.Select(r => r.Sha).SequenceEqual(originalOrder)
        && Rows.All(r => r.Action == RebaseAction.Pick || (r.IsReword && r.Message.Trim() == r.Subject));
}
