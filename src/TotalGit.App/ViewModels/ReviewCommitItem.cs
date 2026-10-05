using CommunityToolkit.Mvvm.ComponentModel;

namespace TotalGit.App.ViewModels;

/// <summary>A commit in a review's commit list: pick it to see only its changes.</summary>
public sealed partial class ReviewCommitItem(string sha, string summary, string author) : ObservableObject
{
    public string Sha { get; } = sha;

    /// <summary>"abc1234 subject".</summary>
    public string Summary { get; } = summary;
    public string Author { get; } = author;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
