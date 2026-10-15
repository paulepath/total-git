using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using TotalGit.App.Services;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

/// <summary>The stand-alone merge window (<c>TotalGit --merge &lt;file&gt;</c>): the merge tool for one file, nothing else.</summary>
public sealed class MergeWindow : Window
{
    private readonly LayoutTransformControl _zoomHost;

    public MergeWindow(string path)
    {
        Title = $"Merge {path} - Total Git";
        MinWidth = 800;
        MinHeight = 450;
        // As big as the screen allows, up to a comfortable size.
        var area = Screens.Primary?.WorkingArea;
        var scale = Screens.Primary?.Scaling ?? 1;
        Width = area is { } a ? Math.Min(1400, a.Width / scale - 40) : 1400;
        Height = area is { } b ? Math.Min(900, b.Height / scale - 40) : 900;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#1C1F24"));

        _zoomHost = new LayoutTransformControl { Child = new MergeToolView() };
        Content = _zoomHost;
        ApplyZoom();
        Zoom.Changed += ApplyZoom;

        // Zoom, as in the main window (not remembered here): Ctrl + wheel and Ctrl +/-/0.
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
        // Escape closes without saving, unless something inside used it (e.g. cancelling a line edit).
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
            Close();
            e.Handled = true;
        };
    }

    private void ApplyZoom()
    {
        var z = Zoom.Level;
        _zoomHost.LayoutTransform = Math.Abs(z - 1) < 0.001 ? null : new ScaleTransform(z, z);
    }

    protected override void OnClosed(EventArgs e)
    {
        Zoom.Changed -= ApplyZoom;
        base.OnClosed(e);
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
        MainWindow.Zoomed(new ConfirmDialog(title, message, null, confirmText)).ShowDialog<bool>(this);

    public Task ShowMessageAsync(string title, string message) =>
        MainWindow.Zoomed(new ChoiceDialog(title, message, null, [new DialogChoice("OK", IsPrimary: true)])).ShowDialog<int?>(this);
}
