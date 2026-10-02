using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;

namespace TotalGit.App.ViewModels;

/// <summary>
/// Picks a repository's tab colour and icon: swatches (or none), a searchable grid of the icon library (drawn in
/// the chosen colour), uploads, and a live preview of the tab.
/// </summary>
public sealed partial class TabStyleViewModel : ObservableObject
{
    public static readonly string[] Palette =
        ["#E5484D", "#F76B15", "#E8B339", "#4CC38A", "#12A594", "#5AA9F2", "#2D7BF4", "#A371F7", "#E879F9", "#8A9099"];

    private bool _refreshingIcons;

    public TabStyleViewModel(string name, string? colour, string? iconId)
    {
        Name = name;
        Colour = colour;
        IconId = iconId;
        Colours =
        [
            new(null, new SolidColorBrush(Color.Parse("#30353C")), "No colour"),
            .. Palette.Select(h => new ColourChoice(h, new SolidColorBrush(Color.Parse(h)), h)),
        ];
        RefreshIcons();
    }

    public string Name { get; }
    public string Title => $"Tab colour and icon: {Name}";
    public IReadOnlyList<ColourChoice> Colours { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewTint), nameof(PreviewAccent), nameof(PreviewIcon), nameof(HasPreviewIcon), nameof(HasNoPreviewIcon), nameof(ColourText))]
    public partial string? Colour { get; set; }

    partial void OnColourChanged(string? value) => RefreshIcons();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewIcon), nameof(HasPreviewIcon), nameof(HasNoPreviewIcon))]
    public partial string? IconId { get; set; }

    partial void OnIconIdChanged(string? value) => OnPropertyChanged(nameof(SelectedIcon));

    /// <summary>The colour as typed (#RRGGBB); only a valid colour is taken.</summary>
    public string ColourText
    {
        get => Colour ?? "";
        set
        {
            if (string.IsNullOrWhiteSpace(value)) Colour = null;
            else if (Color.TryParse(value.Trim(), out _)) Colour = value.Trim();
        }
    }

    public IBrush? PreviewTint => TabStyle.Tint(Colour, 0.32);
    public IBrush? PreviewAccent => TabStyle.Accent(Colour);
    public Bitmap? PreviewIcon => IconId is { } id ? IconLibrary.Get(id, Colour) : null;
    public bool HasPreviewIcon => PreviewIcon is not null;
    public bool HasNoPreviewIcon => PreviewIcon is null;

    [ObservableProperty]
    public partial IReadOnlyList<IconChoice> Icons { get; private set; } = [];

    [ObservableProperty]
    public partial string IconFilter { get; set; } = "";

    partial void OnIconFilterChanged(string value) => RefreshIcons();

    [ObservableProperty]
    public partial string? UploadError { get; set; }

    public IconChoice? SelectedIcon
    {
        get => Icons.FirstOrDefault(i => i.Id == IconId);
        set
        {
            // While the list is rebuilt the picker reports its old selection; only a user's pick counts.
            if (_refreshingIcons || value is null || value.Id == IconId) return;
            IconId = value.Id;
        }
    }

    private void RefreshIcons()
    {
        var filter = IconFilter.Trim();
        _refreshingIcons = true;
        try
        {
            Icons = IconLibrary.All()
                .Where(i => filter.Length == 0 || i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Select(i => new IconChoice(i, IconLibrary.Get(i.Id, i.CanRecolour ? Colour : null)))
                .ToList();
            OnPropertyChanged(nameof(SelectedIcon));
        }
        finally
        {
            _refreshingIcons = false;
        }
        OnPropertyChanged(nameof(PreviewIcon));
    }

    [RelayCommand]
    private void PickColour(ColourChoice choice) => Colour = choice.Hex;

    [RelayCommand]
    private void ClearIcon() => IconId = null;

    [RelayCommand]
    private void Reset()
    {
        Colour = null;
        IconId = null;
    }

    public void AddCustomIcon(string path)
    {
        var (id, error) = IconLibrary.AddCustom(path);
        UploadError = error;
        if (id is null) return;
        RefreshIcons();
        IconId = id;
    }
}
