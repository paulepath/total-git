using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;

namespace TotalGit.App.ViewModels;

// The Visual Studio button's menu: install or update the Total Git extension for Visual Studio (bundled with each release).
public sealed partial class RepositoryViewModel
{
    /// <summary>Running from source there's no extension to install, so the menu isn't offered.</summary>
    public bool OffersVisualStudioExtension => VisualStudioExtension.IsBundled;

    [ObservableProperty]
    public partial string VisualStudioExtensionText { get; set; } = "Install the Total Git extension for Visual Studio";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallVisualStudioExtensionCommand))]
    public partial bool CanInstallVisualStudioExtension { get; set; } = true;

    /// <summary>Reads what's installed in Visual Studio now (the menu is about to open).</summary>
    public void RefreshVisualStudioExtension()
    {
        var bundled = VisualStudioExtension.BundledVersion;
        var installed = VisualStudioExtension.InstalledVersion();
        (VisualStudioExtensionText, CanInstallVisualStudioExtension) = installed switch
        {
            null => ("Install the Total Git extension for Visual Studio", true),
            _ when installed < bundled => ($"Update the Total Git extension for Visual Studio ({installed} → {bundled})", true),
            _ => ($"Total Git extension {installed} is installed in Visual Studio", false),
        };
    }

    [RelayCommand(CanExecute = nameof(CanInstallVisualStudioExtension))]
    private void InstallVisualStudioExtension()
    {
        if (VisualStudioExtension.FindInstaller() is not { } installer)
        {
            ShowError("Visual Studio 2022 or later wasn't found, so there's nothing to install the extension into.");
            return;
        }
        try
        {
            VisualStudioExtension.StartInstall(installer);
            ShowInfo(VisualStudioExtension.VisualStudioRunning()
                ? "Visual Studio's installer is open. It finishes once Visual Studio is closed (it offers to close it), then the "
                  + "Worktrees window is under View > Other Windows, and Tools > Total Git."
                : "Visual Studio's installer is open. Once it's done, the Worktrees window is under View > Other Windows, and Tools > Total Git.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowError($"Couldn't start Visual Studio's installer: {ex.Message}");
        }
    }
}
