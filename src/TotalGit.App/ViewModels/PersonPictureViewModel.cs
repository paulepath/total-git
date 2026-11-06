using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Avatars;

namespace TotalGit.App.ViewModels;

/// <summary>Someone a picture can be chosen for: their name, and their commit email and/or GitHub login.</summary>
/// <param name="AvatarUrl">Where GitHub keeps their own picture, if known (shown until another is chosen).</param>
public sealed record PersonRef(string Name, string? Email = null, string? Login = null, string? AvatarUrl = null);

/// <summary>A built-in picture in the dialog's grid.</summary>
public sealed class PresetChoice(AvatarPreset preset)
{
    private Bitmap? _thumbnail;

    public AvatarPreset Preset { get; } = preset;
    public string Label => Preset.Label;
    public Bitmap? Thumbnail => _thumbnail ??= TryThumbnail();

    private Bitmap? TryThumbnail()
    {
        try { return AvatarImages.PresetThumbnail(Preset); }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or InvalidDataException) { return null; }
    }
}

/// <summary>
/// Chooses a picture for someone (their own file, a pasted one, or a built-in one) and which emails and logins it's
/// used for. Saved on this PC only, in <see cref="CustomAvatars"/>.
/// </summary>
public sealed partial class PersonPictureViewModel : ObservableObject
{
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})(?:\[bot\])?$")]
    private static partial Regex LoginPattern();

    private readonly CustomAvatars _store;
    private readonly CustomAvatar? _existing;
    private byte[]? _png;
    private string? _presetId;

    public PersonPictureViewModel(AvatarCache avatars, PersonRef person, Bitmap? current)
    {
        _store = avatars.Custom;
        Name = person.Name;
        _existing = _store.Find(person.Email, person.Login);
        Preview = current;

        // Where it's used: what's chosen already, who it was opened for, and what GitHub told us goes with them.
        var addresses = new List<string>();
        void Add(string? a)
        {
            if (!string.IsNullOrWhiteSpace(a) && !addresses.Contains(a.Trim(), StringComparer.OrdinalIgnoreCase)) addresses.Add(a.Trim());
        }
        foreach (var a in _existing?.Emails ?? []) Add(a);
        foreach (var a in _existing?.Logins ?? []) Add(a);
        Add(person.Email);
        Add(person.Login);
        if (person.Email is { } email) Add(_store.LoginFor(email));
        if (person.Login is { } login) foreach (var e in _store.EmailsFor(login)) Add(e);
        Addresses = new(addresses);
        Addresses.CollectionChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();

        Initials = AvatarIdentity.Initials(Name);
        InitialsBrush = new SolidColorBrush(HslColor.FromHsl(AvatarIdentity.Hue(person.Email ?? person.Login ?? Name), 0.45, 0.42).ToRgb());
        Presets = [.. AvatarImages.Presets.Select(p => new PresetChoice(p))];
    }

    public string Name { get; }
    public string Title => $"Picture for {Name}";
    public string Initials { get; }
    public IBrush InitialsBrush { get; }

    /// <summary>The picture as it will look: the new one, else the one shown now (null: initials).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial Bitmap? Preview { get; private set; }

    public bool HasPreview => Preview is not null;

    /// <summary>A chosen picture is kept for them now, so it can be taken away.</summary>
    public bool HasCustom => _existing is not null;

    /// <summary>The commit emails and GitHub logins it's used for.</summary>
    public ObservableCollection<string> Addresses { get; }

    public IReadOnlyList<PresetChoice> Presets { get; }
    public bool HasPresets => Presets.Count > 0;

    [ObservableProperty]
    public partial PresetChoice? SelectedPreset { get; set; }

    [ObservableProperty]
    public partial string NewAddress { get; set; } = "";

    /// <summary>Why the last picture or address wasn't taken.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    partial void OnSelectedPresetChanged(PresetChoice? value)
    {
        if (value is null) return;
        try
        {
            Use(AvatarImages.PresetPng(value.Preset), value.Preset.Id);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or InvalidDataException)
        {
            Error = "That built-in picture couldn't be loaded.";
        }
    }

    /// <summary>A picture file the user picked or dropped.</summary>
    public void UseFile(string path)
    {
        try
        {
            Use(AvatarImages.FromFile(path), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Error = ex is InvalidDataException ? ex.Message : $"Couldn't read {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    /// <summary>A picture pasted or dropped as an image rather than a file.</summary>
    public void UseBitmap(Bitmap bitmap) => Use(AvatarImages.ToSquarePng(bitmap), null);

    public void ShowError(string message) => Error = message;

    private void Use(byte[] png, string? presetId)
    {
        _png = png;
        _presetId = presetId;
        Preview = AvatarImages.Decode(png);
        Error = null;
        if (presetId is null) SelectedPreset = null;
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AddAddress()
    {
        var a = NewAddress.Trim();
        if (a.Length == 0) return;
        var isEmail = a.Contains('@') && a.IndexOf('@') > 0 && a.IndexOf('@') < a.Length - 1;
        if (!isEmail && !LoginPattern().IsMatch(a))
        {
            Error = $"\"{a}\" isn't an email or a GitHub login.";
            return;
        }
        if (!Addresses.Contains(a, StringComparer.OrdinalIgnoreCase)) Addresses.Add(a);
        NewAddress = "";
        Error = null;
    }

    [RelayCommand]
    private void RemoveAddress(string address) => Addresses.Remove(address);

    private bool CanSave() => Addresses.Count > 0 && (_png is not null || _existing is not null);

    /// <summary>Keeps the picture for its emails and logins. The dialog closes when it returns true.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var png = _png;
        var presetId = _png is null ? _existing?.PresetId : _presetId;
        try
        {
            png ??= File.ReadAllBytes(_store.ImagePath(_existing!));
            _store.Set(_existing?.Id, Name, Addresses.Where(a => a.Contains('@')), Addresses.Where(a => !a.Contains('@')), png, presetId);
            Saved = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "Couldn't save the picture: " + ex.Message;
        }
    }

    /// <summary>Takes the chosen picture away, so they show their own (or initials) again.</summary>
    [RelayCommand]
    private void RemoveCustom()
    {
        if (_existing is null) return;
        _store.Remove(_existing.Id);
        Saved = true;
    }

    /// <summary>Set once the picture was saved or removed: the dialog closes.</summary>
    [ObservableProperty]
    public partial bool Saved { get; private set; }
}
