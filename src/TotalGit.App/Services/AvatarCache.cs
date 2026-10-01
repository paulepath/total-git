using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TotalGit.Core.Avatars;

namespace TotalGit.App.Services;

/// <summary>Decoded avatar bitmaps keyed by email, shared by every view. Lives on the UI thread.</summary>
public sealed class AvatarCache(AvatarService service)
{
    private readonly Dictionary<string, Bitmap?> _bitmaps = [];
    private readonly Dictionary<string, Task<Bitmap?>> _pending = [];

    /// <summary>Raised on the UI thread whenever a new avatar has finished loading.</summary>
    public event Action? Updated;

    /// <summary>Returns the avatar if already loaded, otherwise starts loading it and returns null.</summary>
    public Bitmap? TryGet(string email, (string Owner, string Repo)? gitHubRepo, string? sampleSha)
    {
        var key = AvatarIdentity.NormalizeEmail(email);
        if (_bitmaps.TryGetValue(key, out var bitmap)) return bitmap;
        _ = GetAsync(email, gitHubRepo, sampleSha);
        return null;
    }

    public Task<Bitmap?> GetAsync(string email, (string Owner, string Repo)? gitHubRepo, string? sampleSha)
    {
        var key = AvatarIdentity.NormalizeEmail(email);
        if (_bitmaps.TryGetValue(key, out var bitmap)) return Task.FromResult(bitmap);
        if (_pending.TryGetValue(key, out var task)) return task;

        task = LoadAsync(key, email, gitHubRepo, sampleSha);
        _pending[key] = task;
        return task;
    }

    /// <summary>An avatar from a known address (pull request people) if loaded; otherwise starts loading it and returns null.</summary>
    public Bitmap? TryGetUrl(string url)
    {
        var key = "url:" + url;
        if (_bitmaps.TryGetValue(key, out var bitmap)) return bitmap;
        if (!_pending.ContainsKey(key)) _pending[key] = LoadUrlAsync(key, url);
        return null;
    }

    private async Task<Bitmap?> LoadUrlAsync(string key, string url) => await Decode(key, await service.GetAvatarByUrlAsync(url));

    private async Task<Bitmap?> LoadAsync(string key, string email, (string Owner, string Repo)? gitHubRepo, string? sampleSha) =>
        await Decode(key, await service.GetAvatarAsync(email, gitHubRepo, sampleSha));

    /// <summary>Decodes downloaded image bytes and stores the bitmap (null when there are none) on the UI thread.</summary>
    private async Task<Bitmap?> Decode(string key, byte[]? bytes)
    {
        Bitmap? bitmap = null;
        if (bytes is not null)
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                bitmap = Bitmap.DecodeToWidth(stream, 64);
            }
            catch (Exception)
            {
                // Unsupported or corrupt image: callers fall back to initials.
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _bitmaps[key] = bitmap;
            _pending.Remove(key);
            Updated?.Invoke();
        });
        return bitmap;
    }
}
