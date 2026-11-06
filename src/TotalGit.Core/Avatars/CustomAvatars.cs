using System.Text.Json;

namespace TotalGit.Core.Avatars;

/// <summary>A picture chosen for someone, used for the commit emails and GitHub logins it lists.</summary>
/// <param name="Id">Names its image file, <c>&lt;Id&gt;.png</c>.</param>
/// <param name="Version">Goes up whenever the picture changes, so cached copies are refreshed.</param>
/// <param name="PresetId">The built-in picture it came from, if it did.</param>
public sealed record CustomAvatar(string Id, string Name, IReadOnlyList<string> Emails, IReadOnlyList<string> Logins, int Version, string? PresetId = null);

/// <summary>
/// Pictures chosen for people on this PC, beating GitHub, Gravatar and initials, plus which GitHub login goes with
/// which commit email (learned whenever GitHub says), so a picture set on one is found for the other. Kept in
/// <c>people.json</c> and <c>links.json</c> next to one PNG per person. Thread-safe.
/// </summary>
public sealed class CustomAvatars
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _dir;
    private readonly string _peopleFile;
    private readonly string _linksFile;
    private readonly Lock _lock = new();
    private List<CustomAvatar> _people;
    // Lower-cased email → lower-cased login.
    private readonly Dictionary<string, string> _links;

    public CustomAvatars(string dir)
    {
        _dir = dir;
        _peopleFile = Path.Combine(dir, "people.json");
        _linksFile = Path.Combine(dir, "links.json");
        _people = Load<List<CustomAvatar>>(_peopleFile) ?? [];
        _links = new(Load<Dictionary<string, string>>(_linksFile) ?? [], StringComparer.Ordinal);
    }

    /// <summary>Raised (on whichever thread made the change) when a picture is set or removed.</summary>
    public event Action? Changed;

    public IReadOnlyList<CustomAvatar> All
    {
        get { lock (_lock) return [.. _people]; }
    }

    /// <summary>The picture for someone known by their commit email and/or GitHub login, following learned links.</summary>
    public CustomAvatar? Find(string? email, string? login)
    {
        lock (_lock)
        {
            if (_people.Count == 0) return null;
            var emails = new HashSet<string>(StringComparer.Ordinal);
            var logins = new HashSet<string>(StringComparer.Ordinal);
            if (Normal(email) is { } e)
            {
                emails.Add(e);
                if (_links.TryGetValue(e, out var linked)) logins.Add(linked);
            }
            if (Normal(login) is { } l)
            {
                logins.Add(l);
                foreach (var (le, ll) in _links)
                    if (ll == l) emails.Add(le);
            }
            return _people.FirstOrDefault(p => p.Emails.Any(x => emails.Contains(Normal(x)!)) || p.Logins.Any(x => logins.Contains(Normal(x)!)));
        }
    }

    public string ImagePath(CustomAvatar avatar) => Path.Combine(_dir, avatar.Id + ".png");

    /// <summary>
    /// Sets a picture for the given emails and logins: replaces <paramref name="id"/>'s (or any other entry these
    /// addresses belonged to) with one entry.
    /// </summary>
    public CustomAvatar Set(string? id, string name, IEnumerable<string> emails, IEnumerable<string> logins, byte[] png, string? presetId = null)
    {
        var e = emails.Select(Normal).OfType<string>().Distinct().ToList();
        var l = logins.Select(Normal).OfType<string>().Distinct().ToList();
        if (e.Count == 0 && l.Count == 0) throw new ArgumentException("A picture needs an email or a login to belong to.");

        CustomAvatar avatar;
        lock (_lock)
        {
            var existing = id is null ? null : _people.FirstOrDefault(p => p.Id == id);
            // Addresses move to this entry; entries left with none are dropped.
            var others = _people.Where(p => p != existing)
                .Select(p => p with { Emails = [.. p.Emails.Where(x => !e.Contains(Normal(x)!))], Logins = [.. p.Logins.Where(x => !l.Contains(Normal(x)!))] })
                .ToList();
            var dropped = others.Where(p => p.Emails.Count == 0 && p.Logins.Count == 0).ToList();

            avatar = new CustomAvatar(existing?.Id ?? Guid.NewGuid().ToString("N")[..12], name, e, l, (existing?.Version ?? 0) + 1, presetId);
            Directory.CreateDirectory(_dir);
            WriteAtomically(ImagePath(avatar), png);
            _people = [.. others.Except(dropped), avatar];
            Save(_peopleFile, _people);
            foreach (var d in dropped) TryDelete(ImagePath(d));
        }
        Changed?.Invoke();
        return avatar;
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            var avatar = _people.FirstOrDefault(p => p.Id == id);
            if (avatar is null) return;
            _people = [.. _people.Where(p => p != avatar)];
            Save(_peopleFile, _people);
            TryDelete(ImagePath(avatar));
        }
        Changed?.Invoke();
    }

    /// <summary>Remembers that <paramref name="email"/> is GitHub user <paramref name="login"/>.</summary>
    public void Link(string email, string login)
    {
        if (Normal(email) is not { } e || Normal(login) is not { } l) return;
        bool matters;
        lock (_lock)
        {
            if (_links.TryGetValue(e, out var known) && known == l) return;
            _links[e] = l;
            Directory.CreateDirectory(_dir);
            Save(_linksFile, _links);
            // A new link can join a picture to someone it didn't reach before.
            matters = _people.Count > 0;
        }
        if (matters) Changed?.Invoke();
    }

    public string? LoginFor(string email)
    {
        lock (_lock) return Normal(email) is { } e ? _links.GetValueOrDefault(e) : null;
    }

    public IReadOnlyList<string> EmailsFor(string login)
    {
        lock (_lock)
            return Normal(login) is { } l ? [.. _links.Where(kv => kv.Value == l).Select(kv => kv.Key).Order(StringComparer.Ordinal)] : [];
    }

    private static string? Normal(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant();

    private static T? Load<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static void Save<T>(string path, T value)
    {
        try { WriteAtomically(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)); }
        catch (IOException) { }
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
    }
}
