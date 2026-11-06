using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Avatars;

namespace TotalGit.App.ViewModels;

/// <summary>One person with a chosen picture, in the list of them all.</summary>
public sealed class PeoplePictureRow(CustomAvatar avatar, Bitmap? picture)
{
    public CustomAvatar Avatar { get; } = avatar;
    public Bitmap? Picture { get; } = picture;
    public string Name => Avatar.Name;
    public string UsedFor => string.Join(", ", Avatar.Emails.Concat(Avatar.Logins));
}

/// <summary>Every picture chosen for someone on this PC, to change or remove.</summary>
public sealed partial class PeoplePicturesViewModel : ObservableObject, IDisposable
{
    private readonly AvatarCache _avatars;

    public PeoplePicturesViewModel(AvatarCache avatars)
    {
        _avatars = avatars;
        _avatars.Changed += Reload;
        Reload();
    }

    public AvatarCache Avatars => _avatars;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial IReadOnlyList<PeoplePictureRow> People { get; private set; } = [];

    public bool IsEmpty => People.Count == 0;

    private void Reload() =>
        People = [.. _avatars.Custom.All.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Select(a => new PeoplePictureRow(a, Load(a)))];

    private Bitmap? Load(CustomAvatar a)
    {
        try { return AvatarImages.Decode(File.ReadAllBytes(_avatars.Custom.ImagePath(a))); }
        catch (IOException) { return null; }
    }

    /// <summary>The dialog for changing one of them.</summary>
    public PersonPictureViewModel Change(PeoplePictureRow row) =>
        new(_avatars, new PersonRef(row.Name, row.Avatar.Emails.FirstOrDefault(), row.Avatar.Logins.FirstOrDefault()), row.Picture);

    [RelayCommand]
    private void Remove(PeoplePictureRow row) => _avatars.Custom.Remove(row.Avatar.Id);

    public void Dispose() => _avatars.Changed -= Reload;
}
