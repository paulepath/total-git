using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TotalGit.Core.Avatars;

namespace TotalGit.App.Services;

/// <summary>Something showing an avatar that asks for it again when chosen pictures change.</summary>
public interface IAvatarWatcher
{
    void AvatarsChanged();
}

/// <summary>
/// Decoded avatar bitmaps shared by every view, keyed by commit email or by GitHub user. A picture chosen for someone
/// (<see cref="Custom"/>) wins over the ones found online. Lives on the UI thread.
/// </summary>
public sealed class AvatarCache
{
    private readonly AvatarService _service;
    private readonly Dictionary<string, Bitmap?> _bitmaps = [];
    private readonly Dictionary<string, Task<Bitmap?>> _pending = [];
    // Bumped when the chosen pictures change, so loads started before then don't store what they found.
    private int _generation;

    public AvatarCache(AvatarService service)
    {
        _service = service;
        service.Custom.Changed += () => Dispatcher.UIThread.Post(OnCustomChanged);
    }

    /// <summary>Pictures chosen for people on this PC.</summary>
    public CustomAvatars Custom => _service.Custom;

    /// <summary>Raised on the UI thread whenever a new avatar has finished loading.</summary>
    public event Action? Updated;

    /// <summary>
    /// Raised on the UI thread when someone's chosen picture was set or removed: avatars already shown may be out of
    /// date, so ask again.
    /// </summary>
    public event Action? Changed;

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
        return Get(key, async () => await ChosenAsync(email, null) ?? await _service.GetAvatarAsync(email, gitHubRepo, sampleSha));
    }

    /// <summary>A hosting service user's avatar (pull request people) if loaded; otherwise starts loading it and returns null.</summary>
    /// <param name="url">Where the host keeps their picture, if anywhere.</param>
    public Bitmap? TryGetUser(string? login, string? url)
    {
        if (_bitmaps.TryGetValue(UserKey(login, url), out var bitmap)) return bitmap;
        _ = GetUserAsync(login, url);
        return null;
    }

    /// <summary>A hosting service user's avatar, loading it when needed (null when there's none).</summary>
    public Task<Bitmap?> GetUserAsync(string? login, string? url) =>
        Get(UserKey(login, url), async () => await ChosenAsync(null, login) ?? (url is null ? null : await _service.GetAvatarByUrlAsync(url)));

    private static string UserKey(string? login, string? url) => $"user:{login?.ToLowerInvariant()}|{url}";

    private Task<Bitmap?> Get(string key, Func<Task<byte[]?>> load)
    {
        if (_bitmaps.TryGetValue(key, out var bitmap)) return Task.FromResult(bitmap);
        if (_pending.TryGetValue(key, out var task)) return task;
        task = LoadAsync(key, load, _generation);
        _pending[key] = task;
        return task;
    }

    /// <summary>The picture chosen for someone, read from disk; null when there's none.</summary>
    private async Task<byte[]?> ChosenAsync(string? email, string? login)
    {
        if (Custom.Find(email, login) is not { } chosen) return null;
        try
        {
            return await File.ReadAllBytesAsync(Custom.ImagePath(chosen));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void OnCustomChanged()
    {
        _generation++;
        _bitmaps.Clear();
        _pending.Clear();
        Changed?.Invoke();
        _watchers.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in _watchers.ToList())
            if (w.TryGetTarget(out var watcher)) watcher.AvatarsChanged();
        Updated?.Invoke();
    }

    private readonly List<WeakReference<IAvatarWatcher>> _watchers = [];

    /// <summary>
    /// Tells <paramref name="watcher"/> when chosen pictures change, without keeping it alive (for the many short-lived
    /// people in pull request windows).
    /// </summary>
    public void WatchWeakly(IAvatarWatcher watcher) => _watchers.Add(new(watcher));

    /// <summary>Decodes downloaded image bytes and stores the bitmap (null when there are none) on the UI thread.</summary>
    private async Task<Bitmap?> LoadAsync(string key, Func<Task<byte[]?>> load, int generation)
    {
        var bytes = await load();
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
            if (generation != _generation) return;
            _bitmaps[key] = bitmap;
            _pending.Remove(key);
            Updated?.Invoke();
        });
        return bitmap;
    }
}
