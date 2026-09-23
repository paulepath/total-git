using System.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace TotalGit.App.Services;

/// <summary>
/// Updates the installed app from the GitHub Releases that CI publishes on every push to master.
/// Downloads in the background; the new version is applied on restart or when the app closes.
/// Does nothing for dev builds that weren't installed with Setup.exe.
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/paulepath/total-git";

    private readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
    private VelopackAsset? _pending;
    private int _checking;

    /// <summary>Raised (on a background thread) with the new version once it has been downloaded.</summary>
    public event Action<string>? UpdateReady;

    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>The installed version, or "dev" when running a local build.</summary>
    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? "dev";

    public bool HasPendingUpdate => _pending is not null;

    public async Task CheckAndDownloadAsync()
    {
        if (!IsInstalled || _pending is not null) return;
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null) return;
            await _manager.DownloadUpdatesAsync(info);
            _pending = info.TargetFullRelease;
            UpdateReady?.Invoke(_pending.Version.ToString());
        }
        catch (Exception ex)
        {
            // Offline, rate-limited or a bad release: try again at the next check.
            Trace.WriteLine($"Update check failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    public void RestartToUpdate()
    {
        if (_pending is not null) _manager.ApplyUpdatesAndRestart(_pending);
    }

    /// <summary>Applies a downloaded update after the app exits (without restarting it).</summary>
    public void ApplyOnExit()
    {
        if (_pending is not null) _manager.WaitExitThenApplyUpdates(_pending, silent: true, restart: false);
    }
}
