using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TotalGit.App.ViewModels;

// Banners without actions close by themselves: information after a few seconds, errors a little later so there's
// time to read them. Hovering a banner pauses the countdown.
public partial class RepositoryViewModel
{
    private static readonly TimeSpan InfoBannerTime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ErrorBannerTime = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan BannerTick = TimeSpan.FromMilliseconds(100);

    private DispatcherTimer? _bannerTimer;
    private TimeSpan _bannerTotal;
    private TimeSpan _bannerLeft;
    private bool _bannerPaused;

    /// <summary>The banner will close by itself (it has no actions to take).</summary>
    [ObservableProperty]
    public partial bool BannerClosesItself { get; private set; }

    /// <summary>How much of the banner's time is left, 1 → 0, for the countdown line under it.</summary>
    [ObservableProperty]
    public partial double BannerTimeLeft { get; private set; }

    partial void OnBannerChanged(Banner? value)
    {
        _bannerTimer?.Stop();
        BannerClosesItself = value is { HasActions: false };
        if (!BannerClosesItself) return;

        _bannerTotal = _bannerLeft = value!.IsError ? ErrorBannerTime : InfoBannerTime;
        BannerTimeLeft = 1;
        if (_bannerTimer is null)
        {
            _bannerTimer = new DispatcherTimer { Interval = BannerTick };
            _bannerTimer.Tick += (_, _) => OnBannerTick();
        }
        _bannerTimer.Start();
    }

    private void OnBannerTick()
    {
        if (_bannerPaused) return;
        _bannerLeft -= BannerTick;
        BannerTimeLeft = Math.Max(0, _bannerLeft / _bannerTotal);
        if (_bannerLeft > TimeSpan.Zero) return;
        _bannerTimer?.Stop();
        Banner = null;
    }

    /// <summary>The pointer is over the banner: hold it open while it's being read.</summary>
    public void PauseBannerTimer(bool paused) => _bannerPaused = paused;
}
