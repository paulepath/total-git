using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TotalGit.App.Services;

/// <summary>Turns pictures people choose into the square PNGs kept for them, and the built-in ones to choose from.</summary>
public static class AvatarImages
{
    /// <summary>Kept at twice the largest size drawn (64), so it stays sharp when zoomed.</summary>
    public const int Size = 128;

    public const long MaxFileBytes = 10 * 1024 * 1024;

    /// <summary>The middle square of <paramref name="source"/>, scaled to <see cref="Size"/>, as PNG.</summary>
    public static byte[] ToSquarePng(Bitmap source)
    {
        var (w, h) = (source.Size.Width, source.Size.Height);
        var side = Math.Min(w, h);
        var from = new Rect((w - side) / 2, (h - side) / 2, side, side);
        using var target = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var ctx = target.CreateDrawingContext())
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            ctx.DrawImage(source, from, new Rect(0, 0, Size, Size));
        using var png = new MemoryStream();
        target.Save(png, new PngBitmapEncoderOptions());
        return png.ToArray();
    }

    /// <exception cref="InvalidDataException">Too big, or not a picture; the message says which.</exception>
    public static byte[] FromFile(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("That picture is over 10 MB.");
        using var stream = File.OpenRead(path);
        return FromStream(stream);
    }

    /// <exception cref="InvalidDataException">Not a picture.</exception>
    public static byte[] FromStream(Stream stream)
    {
        Bitmap bitmap;
        try
        {
            bitmap = new Bitmap(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException("That isn't a picture Total Git can read (try PNG or JPG).", ex);
        }
        using (bitmap) return ToSquarePng(bitmap);
    }

    public static Bitmap? Decode(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static IReadOnlyList<AvatarPreset>? _presets;

    /// <summary>The built-in pictures, from <c>Assets/AvatarPresets/presets.json</c>; none when it's missing.</summary>
    public static IReadOnlyList<AvatarPreset> Presets => _presets ??= LoadPresets();

    private static IReadOnlyList<AvatarPreset> LoadPresets()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(PresetFolder + "presets.json"));
            return JsonSerializer.Deserialize<List<AvatarPreset>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or JsonException or IOException)
        {
            return [];
        }
    }

    private const string PresetFolder = "avares://TotalGit/Assets/AvatarPresets/";

    /// <summary>A built-in picture as the PNG kept for someone.</summary>
    public static byte[] PresetPng(AvatarPreset preset)
    {
        using var stream = AssetLoader.Open(new Uri(PresetFolder + preset.File));
        return FromStream(stream);
    }

    /// <summary>A built-in picture small enough for the grid.</summary>
    public static Bitmap PresetThumbnail(AvatarPreset preset)
    {
        using var stream = AssetLoader.Open(new Uri(PresetFolder + preset.File));
        return Bitmap.DecodeToWidth(stream, 64, BitmapInterpolationMode.HighQuality);
    }
}

/// <summary>A built-in picture: its id, the name shown as its tooltip, and its file in <c>Assets/AvatarPresets</c>.</summary>
public sealed record AvatarPreset(string Id, string Label, string File);
