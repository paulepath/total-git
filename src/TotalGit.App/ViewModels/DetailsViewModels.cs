using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.Core.Git;

namespace TotalGit.App.ViewModels;

/// <summary>A file row in the commit details or staging lists.</summary>
public sealed class FileChangeItem(FileChange change, bool staged)
{
    private static readonly IBrush Added = new SolidColorBrush(Color.Parse("#4CC38A"));
    private static readonly IBrush Modified = new SolidColorBrush(Color.Parse("#E8B339"));
    private static readonly IBrush Deleted = new SolidColorBrush(Color.Parse("#F26B6B"));
    private static readonly IBrush Renamed = new SolidColorBrush(Color.Parse("#5AA9F2"));
    private static readonly IBrush Other = new SolidColorBrush(Color.Parse("#B084F5"));

    public FileChange Change { get; } = change;
    public bool IsStaged { get; } = staged;
    public string FileName => Change.DisplayName;
    public string? Folder => Change.Directory is { Length: > 0 } d ? d : null;
    public bool HasFolder => Folder is not null;
    public string Path => Change.Path;
    public string ToolTip => Change.OldPath is { } old ? $"{old} → {Change.Path}" : Change.Path;

    public string StatusLetter => Change.Kind switch
    {
        ChangeKind.Added => "A",
        ChangeKind.Deleted => "D",
        ChangeKind.Renamed => "R",
        ChangeKind.TypeChanged => "T",
        ChangeKind.Untracked => "U",
        ChangeKind.Conflicted => "!",
        _ => "M",
    };

    public IBrush StatusBrush => Change.Kind switch
    {
        ChangeKind.Added or ChangeKind.Untracked => Added,
        ChangeKind.Deleted => Deleted,
        ChangeKind.Renamed => Renamed,
        ChangeKind.Modified => Modified,
        _ => Other,
    };

    public string? Stats => Change is { Additions: 0, Deletions: 0 } ? (Change.IsBinary ? "binary" : null) : $"+{Change.Additions} -{Change.Deletions}";
    public bool HasStats => Stats is not null;
}

public sealed partial class CommitDetailsViewModel : ObservableObject
{
    public CommitDetailsViewModel(CommitDetails details, Action<string> selectCommit)
    {
        Details = details;
        var lines = details.FullMessage.TrimEnd().Split('\n', 2);
        Summary = lines[0].Trim();
        Body = lines.Length > 1 ? lines[1].Trim() : null;
        Files = details.Files.Select(f => new FileChangeItem(f, false)).ToArray();
        Parents = details.Commit.ParentShas.Select(p => new ParentLink(p, new RelayCommand(() => selectCommit(p)))).ToArray();
    }

    public CommitDetails Details { get; }
    public string Sha => Details.Commit.Sha;
    public string ShortSha => Details.Commit.ShortSha;
    public string Summary { get; }
    public string? Body { get; }
    public bool HasBody => !string.IsNullOrEmpty(Body);
    public string AuthorName => Details.Commit.AuthorName;
    public string AuthorEmail => Details.Commit.AuthorEmail;
    public string AuthorDate => Details.Commit.AuthorDate.LocalDateTime.ToString("f");
    public bool CommitterDiffers => Details.CommitterEmail != Details.Commit.AuthorEmail || Details.CommitterName != Details.Commit.AuthorName;
    public string CommitterText => $"committed by {Details.CommitterName} · {Details.CommitterDate.LocalDateTime:g}";
    public string Initials => Core.Avatars.AvatarIdentity.Initials(AuthorName);
    public IReadOnlyList<FileChangeItem> Files { get; }
    public string FileCountText => Files.Count == 1 ? "1 file changed" : $"{Files.Count} files changed";
    public IReadOnlyList<ParentLink> Parents { get; }

    [ObservableProperty]
    public partial Bitmap? Avatar { get; set; }

    [ObservableProperty]
    public partial FileChangeItem? SelectedFile { get; set; }

    public sealed record ParentLink(string Sha, IRelayCommand Command)
    {
        public string ShortSha => Sha[..7];
    }
}

public sealed partial class StagingViewModel : ObservableObject
{
    public ObservableCollection<FileChangeItem> Unstaged { get; } = [];
    public ObservableCollection<FileChangeItem> Staged { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    [ObservableProperty]
    public partial FileChangeItem? SelectedFile { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial bool IsBusy { get; set; }

    public string UnstagedHeader => $"Unstaged files ({Unstaged.Count})";
    public string StagedHeader => $"Staged files ({Staged.Count})";
    public bool HasUnstaged => Unstaged.Count > 0;
    public bool HasStaged => Staged.Count > 0;

    public required Func<IEnumerable<string>, Task> Stage { get; init; }
    public required Func<IEnumerable<string>, Task> Unstage { get; init; }
    public required Func<Task> StageAll { get; init; }
    public required Func<Task> UnstageAll { get; init; }
    public required Func<string, Task<bool>> Commit { get; init; }

    public void Update(WorkingTreeStatus status)
    {
        var selected = SelectedFile;
        Replace(Unstaged, status.Unstaged.Select(f => new FileChangeItem(f, false)));
        Replace(Staged, status.Staged.Select(f => new FileChangeItem(f, true)));
        OnPropertyChanged(nameof(UnstagedHeader));
        OnPropertyChanged(nameof(StagedHeader));
        OnPropertyChanged(nameof(HasUnstaged));
        OnPropertyChanged(nameof(HasStaged));
        CommitCommand.NotifyCanExecuteChanged();

        if (selected is not null)
        {
            var list = selected.IsStaged ? Staged : Unstaged;
            SelectedFile = list.FirstOrDefault(f => f.Path == selected.Path);
        }
    }

    private static void Replace(ObservableCollection<FileChangeItem> target, IEnumerable<FileChangeItem> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    [RelayCommand]
    private Task StageFileAsync(FileChangeItem item) => Run(() => Stage([item.Path]));

    [RelayCommand]
    private Task UnstageFileAsync(FileChangeItem item) => Run(() => Unstage([item.Path]));

    [RelayCommand]
    private Task StageAllFilesAsync() => Run(StageAll);

    [RelayCommand]
    private Task UnstageAllFilesAsync() => Run(UnstageAll);

    private bool CanCommit() => !IsBusy && HasStaged && !string.IsNullOrWhiteSpace(Summary);

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        var message = string.IsNullOrWhiteSpace(Description) ? Summary.Trim() : $"{Summary.Trim()}\n\n{Description.Trim()}";
        var ok = false;
        await Run(async () => ok = await Commit(message));
        if (ok)
        {
            Summary = "";
            Description = "";
        }
    }

    private async Task Run(Func<Task> action)
    {
        IsBusy = true;
        try { await action(); }
        finally { IsBusy = false; }
    }
}
