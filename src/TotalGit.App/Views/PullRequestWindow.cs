using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using TotalGit.App.Services;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

/// <summary>A pull request's own review window (one per pull request; not modal).</summary>
public sealed class PullRequestWindow : Window, IReviewDialogs
{
    private readonly PullRequestReviewViewModel _vm;
    private readonly AppSettings _settings;
    private readonly LayoutTransformControl _zoomHost;

    public PullRequestWindow(PullRequestReviewViewModel vm, AppSettings settings, Window? owner)
    {
        _vm = vm;
        _settings = settings;
        DataContext = vm;
        Title = vm.WindowTitle;
        Width = settings.ReviewWindowWidth;
        Height = settings.ReviewWindowHeight;
        MinWidth = 900;
        MinHeight = 500;
        if (settings.ReviewWindowMaximized) WindowState = WindowState.Maximized;
        // Over the main window, on its screen, and no bigger than that screen (once shown, when sizes are known).
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opened += (_, _) => PlaceOver(owner);
        Background = new SolidColorBrush(Color.Parse("#1C1F24"));
        if (owner?.Icon is { } icon) Icon = icon;

        _zoomHost = new LayoutTransformControl { Child = new PullRequestReviewView() };
        Content = _zoomHost;
        ApplyZoom();
        Zoom.Changed += ApplyZoom;

        // Zoom, as in the main window: Ctrl + wheel and Ctrl +/-/0. The level is the app's, so both windows follow it.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add:
                    Zoom.ZoomIn();
                    break;
                case Key.OemMinus or Key.Subtract:
                    Zoom.ZoomOut();
                    break;
                case Key.D0 or Key.NumPad0:
                    Zoom.Reset();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;
            if (e.Delta.Y > 0) Zoom.ZoomIn();
            else Zoom.ZoomOut();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        vm.Dialogs = this;
        vm.CloseRequested += Close;
    }

    private void PlaceOver(Window? owner)
    {
        if (owner is null || WindowState == WindowState.Maximized || owner.Screens.ScreenFromWindow(owner) is not { } screen) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        Width = Math.Min(Width, area.Width / scale - 40);
        Height = Math.Min(Height, area.Height / scale - 40);
        var ownerScale = owner.RenderScaling;
        var centreX = owner.Position.X + owner.Bounds.Width * ownerScale / 2;
        var centreY = owner.Position.Y + owner.Bounds.Height * ownerScale / 2;
        var x = Math.Clamp(centreX - Width * scale / 2, area.X, area.Right - Width * scale);
        var y = Math.Clamp(centreY - Height * scale / 2, area.Y, area.Bottom - Height * scale);
        Position = new Avalonia.PixelPoint((int)x, (int)y);
    }

    private void ApplyZoom()
    {
        var z = Zoom.Level;
        _zoomHost.LayoutTransform = Math.Abs(z - 1) < 0.001 ? null : new ScaleTransform(z, z);
    }

    protected override void OnClosed(EventArgs e)
    {
        Zoom.Changed -= ApplyZoom;
        _vm.CloseRequested -= Close;
        _vm.Dialogs = null;
        base.OnClosed(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _settings.ReviewWindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.ReviewWindowWidth = Bounds.Width;
            _settings.ReviewWindowHeight = Bounds.Height;
        }
        _settings.Save();
        base.OnClosing(e);
    }

    public Task<bool> ShowFormAsync(FormSpec spec) => MainWindow.Zoomed(new FormDialog(spec)).ShowDialog<bool>(this);
}
