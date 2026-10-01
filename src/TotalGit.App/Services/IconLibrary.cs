using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TotalGit.Core.Git;

namespace TotalGit.App.Services;

/// <summary>An icon that can be given to a branch rule.</summary>
/// <param name="Id"><c>builtin:bug</c>, <c>lucide:rocket</c> or <c>custom:file.png</c>.</param>
/// <param name="CanRecolour">Drawn in the rule's colour (the line icons); pictures keep their own colours.</param>
public sealed record IconInfo(string Id, string Name, bool CanRecolour);

/// <summary>
/// The icons branch rules can use: the app's own branch pictures, a set of line icons from Lucide
/// (https://lucide.dev, ISC licence, see Assets/icons/LUCIDE-LICENSE.txt) drawn in any colour, and pictures the user
/// has added. Everything is turned into a bitmap, cached, so the graph, sidebar and dialogs draw them the same way.
/// </summary>
public static class IconLibrary
{
    /// <summary>The colour of a line icon whose rule doesn't pick one.</summary>
    public const string DefaultColour = "#C9CED6";

    private const int Pixels = 48;
    private const long MaxUploadBytes = 1024 * 1024;

    private static readonly string[] Builtins = ["feature", "features", "bug", "bugs", "hot-fix", "main"];
    private static readonly Lazy<Dictionary<string, Geometry[]>> Lucide = new(LoadLucide);
    private static readonly Dictionary<(string Id, string? Colour), Bitmap?> Cache = [];

    public static string CustomFolder => Path.Combine(AppSettings.DataDirectory, "icons");

    /// <summary>Raised after icons or rules change, so views showing branch icons can redraw.</summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();

    /// <summary>The app's pictures, then the line icons, then the user's own.</summary>
    public static IReadOnlyList<IconInfo> All()
    {
        var list = Builtins.Select(b => new IconInfo("builtin:" + b, b, false)).ToList();
        list.AddRange(Lucide.Value.Keys.Order().Select(k => new IconInfo("lucide:" + k, k, true)));
        if (Directory.Exists(CustomFolder))
        {
            list.AddRange(Directory.EnumerateFiles(CustomFolder)
                .Where(IsPicture)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(f => new IconInfo("custom:" + Path.GetFileName(f), Path.GetFileNameWithoutExtension(f), false)));
        }
        return list;
    }

    public static Bitmap? ForRule(BranchRule? rule) => rule?.Icon is { } id ? Get(id, rule.IconColor) : null;

    /// <summary>The icon a branch shows under the active rules, or null for none.</summary>
    public static Bitmap? ForBranch(string? name) => ForRule(BranchRuleSet.Current.Match(name).Rule);

    /// <summary>The icon of a sidebar folder ("bugs", "hot-fix") under the active rules.</summary>
    public static Bitmap? ForFolder(string path) => ForRule(BranchRuleSet.Current.ForFolder(path));

    /// <summary>An icon as a bitmap (in the colour given, for a line icon), or null when it doesn't exist.</summary>
    public static Bitmap? Get(string id, string? colour = null)
    {
        var key = (id, id.StartsWith("lucide:", StringComparison.Ordinal) ? colour ?? DefaultColour : null);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        Bitmap? bitmap = null;
        try
        {
            bitmap = id.Split(':', 2) switch
            {
                ["builtin", var name] when Builtins.Contains(name) =>
                    new Bitmap(AssetLoader.Open(new Uri($"avares://TotalGit/Assets/branch-{name}.png"))),
                ["lucide", var name] when Lucide.Value.TryGetValue(name, out var geometry) => Draw(geometry, key.Item2!),
                ["custom", var file] when SafeFile(file) is { } path && File.Exists(path) => LoadPicture(path),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            // A missing or unreadable picture just shows no icon.
        }
        Cache[key] = bitmap;
        return bitmap;
    }

    /// <summary>Copies a picture into the user's icon folder; returns its id, or an error to show.</summary>
    public static (string? Id, string? Error) AddCustom(string sourcePath)
    {
        if (!IsPicture(sourcePath)) return (null, "Pick a PNG, JPG, BMP or ICO picture.");
        var info = new FileInfo(sourcePath);
        if (info.Length > MaxUploadBytes) return (null, "That picture is over 1 MB; pick a smaller one.");
        try
        {
            using (var stream = File.OpenRead(sourcePath)) _ = Bitmap.DecodeToWidth(stream, Pixels);
        }
        catch (Exception)
        {
            return (null, "That file couldn't be read as a picture.");
        }

        Directory.CreateDirectory(CustomFolder);
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        var target = Path.Combine(CustomFolder, name + ext);
        for (var n = 2; File.Exists(target); n++) target = Path.Combine(CustomFolder, $"{name}-{n}{ext}");
        File.Copy(sourcePath, target);
        return ("custom:" + Path.GetFileName(target), null);
    }

    private static bool IsPicture(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".ico";

    /// <summary>The file for a custom icon id, refusing anything outside the icon folder.</summary>
    private static string? SafeFile(string file) =>
        file.Length > 0 && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? Path.Combine(CustomFolder, file) : null;

    private static Bitmap LoadPicture(string path)
    {
        using var stream = File.OpenRead(path);
        return Bitmap.DecodeToWidth(stream, Pixels, BitmapInterpolationMode.HighQuality);
    }

    /// <summary>Draws a 24×24 line icon at <see cref="Pixels"/> pixels, as Lucide does: 2-unit round-capped strokes.</summary>
    private static Bitmap Draw(Geometry[] paths, string colour)
    {
        var brush = new SolidColorBrush(Color.TryParse(colour, out var c) ? c : Color.Parse(DefaultColour));
        var pen = new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var bitmap = new RenderTargetBitmap(new PixelSize(Pixels, Pixels), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        using (ctx.PushTransform(Matrix.CreateScale(Pixels / 24.0, Pixels / 24.0)))
        {
            foreach (var path in paths) ctx.DrawGeometry(null, pen, path);
        }
        return bitmap;
    }

    private static Dictionary<string, Geometry[]> LoadLucide()
    {
        using var stream = AssetLoader.Open(new Uri("avares://TotalGit/Assets/icons/lucide.json"));
        var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream) ?? [];
        return raw.ToDictionary(kv => kv.Key, kv => kv.Value.Select(d => (Geometry)Geometry.Parse(d)).ToArray());
    }
}
