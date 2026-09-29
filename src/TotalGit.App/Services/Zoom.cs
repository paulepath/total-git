namespace TotalGit.App.Services;

/// <summary>
/// Whole-UI zoom (Ctrl + wheel, Ctrl +/-, Ctrl 0), e.g. to make the app readable when sharing the screen.
/// The main window scales its content; menus, tooltips and dialogs follow <see cref="Level"/> too.
/// </summary>
public static class Zoom
{
    // Browser-like steps.
    private static readonly double[] Levels = [0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    private static AppSettings? _settings;

    public static double Level { get; private set; } = 1.0;

    public static event Action? Changed;

    /// <summary>Starts from the saved level; later changes are saved there.</summary>
    public static void Initialize(AppSettings settings)
    {
        _settings = settings;
        Set(settings.Zoom, save: false);
    }

    public static void ZoomIn() => Set(Levels.FirstOrDefault(l => l > Level + 0.001, Levels[^1]));

    public static void ZoomOut() => Set(Levels.LastOrDefault(l => l < Level - 0.001, Levels[0]));

    public static void Reset() => Set(1.0);

    private static void Set(double level, bool save = true)
    {
        level = Math.Clamp(level, Levels[0], Levels[^1]);
        if (Math.Abs(level - Level) < 0.001 && save) return;
        Level = level;
        if (save && _settings is not null)
        {
            _settings.Zoom = level;
            _settings.Save();
        }
        Changed?.Invoke();
    }
}
