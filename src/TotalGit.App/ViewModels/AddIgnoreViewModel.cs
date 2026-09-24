using CommunityToolkit.Mvvm.ComponentModel;
using TotalGit.Core.Git;

namespace TotalGit.App.ViewModels;

/// <summary>The "Add to .gitignore" dialog: editable rules with a live list of the files they would hide.</summary>
public sealed partial class AddIgnoreViewModel : ObservableObject
{
    private readonly string _worktree;
    private CancellationTokenSource? _preview;

    public AddIgnoreViewModel(string worktree, string rules)
    {
        _worktree = worktree;
        Rules = rules;
    }

    public static IReadOnlyList<string> Targets { get; } = [".gitignore (shared once committed)", ".git/info/exclude (only this clone)"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanIgnore))]
    public partial string Rules { get; set; }

    [ObservableProperty]
    public partial int TargetIndex { get; set; }

    public IgnoreTarget Target => TargetIndex == 1 ? IgnoreTarget.InfoExclude : IgnoreTarget.GitIgnore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchText))]
    public partial IReadOnlyList<string> Matches { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackedText), nameof(HasTracked))]
    public partial int TrackedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchText))]
    public partial bool IsPreviewing { get; set; }

    public string MatchText => IsPreviewing ? "Checking…" : Matches.Count == 1 ? "1 file matched" : $"{Matches.Count} files matched";
    public bool HasTracked => TrackedCount > 0;
    public string TrackedText => $"{TrackedCount} tracked file{(TrackedCount == 1 ? "" : "s")} also match. Ignore rules don't affect files git already tracks.";

    /// <summary>Rules can be added before any file matches them (e.g. <c>*.log</c>).</summary>
    public bool CanIgnore => !string.IsNullOrWhiteSpace(Rules);

    partial void OnRulesChanged(string value) => _ = PreviewAsync(value);

    private async Task PreviewAsync(string rules)
    {
        _preview?.Cancel();
        var cts = _preview = new CancellationTokenSource();
        IsPreviewing = true;
        try
        {
            await Task.Delay(300, cts.Token);
            var result = await GitActions.PreviewIgnoreAsync(_worktree, rules, cts.Token);
            if (cts.IsCancellationRequested) return;
            Matches = result.Untracked;
            TrackedCount = result.TrackedCount;
            IsPreviewing = false;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
