using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Avatars;
using TotalGit.Core.Git;
using TotalGit.Core.Graph;

namespace TotalGit.App.ViewModels;

/// <summary>Everything the graph control needs to draw one repository.</summary>
public sealed record GraphData(
    GraphLayoutResult Layout,
    IReadOnlyList<RefInfo> Refs,
    (string Owner, string Repo)? GitHubRepo,
    AvatarService Avatars);

public partial class MainWindowViewModel(AvatarService avatars) : ObservableObject
{
    private string? _repositoryPath;

    /// <summary>Set by the view: shows a folder picker and returns the chosen path.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepository), nameof(ShowEmptyState))]
    public partial GraphData? Graph { get; set; }

    [ObservableProperty]
    public partial string RepositoryName { get; set; } = "No repository";

    [ObservableProperty]
    public partial string CurrentBranch { get; set; } = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowEmptyState))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? CommitCountText { get; set; }

    [ObservableProperty]
    public partial string? SelectedSha { get; set; }

    public bool HasRepository => Graph is not null;
    public bool HasError => ErrorMessage is not null;
    public bool ShowEmptyState => Graph is null && !IsLoading && ErrorMessage is null;

    [RelayCommand]
    private async Task OpenRepositoryAsync()
    {
        if (PickFolder is null) return;
        var path = await PickFolder();
        if (path is not null) await LoadAsync(path);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_repositoryPath is not null) await LoadAsync(_repositoryPath);
    }

    public async Task LoadAsync(string path)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var (snapshot, layout) = await Task.Run(() =>
            {
                var s = RepositoryReader.Read(path);
                return (s, GraphLayout.Compute(s.Commits));
            });

            _repositoryPath = snapshot.WorkingDirectory;
            RepositoryName = snapshot.Name;
            CurrentBranch = snapshot.CurrentBranch ?? "(detached HEAD)";
            CommitCountText = snapshot.Commits.Count >= RepositoryReader.DefaultMaxCommits
                ? $"Showing latest {snapshot.Commits.Count:N0} commits"
                : $"{snapshot.Commits.Count:N0} commits";
            Graph = new GraphData(layout, snapshot.Refs, AvatarIdentity.ParseGitHubRemote(snapshot.OriginUrl), avatars);

            new AppSettings { LastRepository = snapshot.WorkingDirectory }.Save();
        }
        catch (Exception ex) when (ex is RepositoryOpenException or LibGit2Sharp.LibGit2SharpException or IOException)
        {
            ErrorMessage = ex.Message;
            Graph = null;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
