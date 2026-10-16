using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;

namespace TotalGit.App.ViewModels;

// The VS Code button's menu: install or update the Total Git extension for VS Code (bundled with each release).
public sealed partial class RepositoryViewModel
{
    /// <summary>Running from source there's no extension to install, so the menu isn't offered.</summary>
    public bool OffersVsCodeExtension => VsCodeExtension.IsBundled;

    [ObservableProperty]
    public partial string VsCodeExtensionText { get; set; } = "Install the Total Git extension for VS Code";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallVsCodeExtensionCommand))]
    public partial bool CanInstallVsCodeExtension { get; set; } = true;

    private bool _installingExtension;

    /// <summary>Reads what's installed in VS Code now (the menu is about to open).</summary>
    public void RefreshVsCodeExtension()
    {
        if (_installingExtension) return;
        var bundled = VsCodeExtension.BundledVersion;
        var installed = VsCodeExtension.InstalledVersion();
        (VsCodeExtensionText, CanInstallVsCodeExtension) = installed switch
        {
            null => ("Install the Total Git extension for VS Code", true),
            _ when installed < bundled => ($"Update the Total Git extension ({installed} → {bundled})", true),
            _ => ($"Total Git extension {installed} is installed", false),
        };
    }

    [RelayCommand(CanExecute = nameof(CanInstallVsCodeExtension))]
    private async Task InstallVsCodeExtensionAsync()
    {
        _installingExtension = true;
        CanInstallVsCodeExtension = false;
        IsBusy = true;
        BusyText = "Installing the VS Code extension…";
        try
        {
            await VsCodeExtension.InstallAsync(_settings.VsCodePath);
            ShowInfo("The Total Git extension is installed. In VS Code windows already open, run Developer: Reload Window "
                     + "(Ctrl+Shift+P) to start it; then look for the Total Git icon on the left.");
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or TimeoutException or System.ComponentModel.Win32Exception
                                       or UnauthorizedAccessException)
        {
            ShowError($"Couldn't install the VS Code extension: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            _installingExtension = false;
            RefreshVsCodeExtension();
        }
    }
}
