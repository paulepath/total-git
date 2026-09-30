using CommunityToolkit.Mvvm.Input;
using TotalGit.Core.Git;

namespace TotalGit.App.ViewModels;

// "Not owned by current user": git refuses repositories whose folder belongs to another account until the user
// trusts the folder (safe.directory). The load error offers to do that and then opens the repository again.
public sealed partial class RepositoryViewModel
{
    // The folder the failed load was for, to retry once it's trusted.
    private string? _loadErrorPath;

    /// <summary>The folder git refused because of its owner, when that's why the repository didn't open.</summary>
    public string? UntrustedPath => SafeDirectory.UntrustedPath(LoadError);

    public bool CanTrustFolder => UntrustedPath is not null;

    partial void OnLoadErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(UntrustedPath));
        OnPropertyChanged(nameof(CanTrustFolder));
    }

    [RelayCommand]
    private async Task TrustFolderAsync()
    {
        if (UntrustedPath is not { } folder || Dialogs is null) return;
        if (!await Dialogs.ConfirmAsync("Trust this folder?",
                $"{folder} belongs to a different Windows account, so git won't open it. This usually happens with " +
                "repositories on another drive, cloned as administrator, or copied from another PC.\n\n" +
                "Trusting it adds the folder to git's safe.directory list in your global git config, so git and " +
                "Total Git open it from now on. Only do this for folders you know.",
                null, "Trust folder"))
            return;

        try
        {
            await SafeDirectory.TrustAsync(folder);
        }
        catch (Exception ex) when (ex is GitCommandException or System.ComponentModel.Win32Exception)
        {
            LoadError = $"Couldn't trust {folder}: {ex.Message}";
            return;
        }
        // Another folder may still be untrusted (e.g. the main repository of a worktree): that shows the next error.
        if ((_loadErrorPath ?? PendingPath) is { } path) await LoadAsync(path);
    }
}
