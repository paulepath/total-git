using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Git;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

/// <summary>A repository opened before, as a tile on a new tab.</summary>
public sealed partial class RecentRepositoryItem : ObservableObject
{
    public RecentRepositoryItem(RecentRepository recent, RepoSettings? style)
    {
        Recent = recent;
        _style = style;
        Exists = Directory.Exists(recent.Path);
        Accent = TabStyle.Accent(style?.TabColor);
        Tint = TabStyle.Tint(style?.TabColor, 0.14);
        Icon = TabStyle.Icon(style);
        if (Exists) _ = LoadBranchAsync();
    }

    private readonly RepoSettings? _style;

    public RecentRepository Recent { get; }
    public string Path => Recent.Path;
    public string Name => _style?.TabName ?? Recent.Name;
    public bool Exists { get; }
    public bool IsMissing => !Exists;
    public IBrush? Accent { get; }
    public IBrush? Tint { get; }
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;
    public bool HasNoIcon => Icon is null;

    public string OpenedText => Recent.LastOpened == DateTimeOffset.MinValue ? "" : "Opened " + DateText.Relative(Recent.LastOpened);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBranch), nameof(BranchIcon), nameof(HasBranchIcon), nameof(BranchDisplay))]
    public partial string? Branch { get; private set; }

    public bool HasBranch => Branch is not null;
    public string BranchDisplay => BranchCategory.Classify(Branch).ShortName;
    public Bitmap? BranchIcon => Branch is null ? null : BranchIcons.ForBranch(Branch);
    public bool HasBranchIcon => BranchIcon is not null;

    private async Task LoadBranchAsync() => Branch = await Task.Run(() => GitHead.Read(Path));
}

/// <summary>How a repository's tabs look: its colour (as a tint and a top bar) and its icon.</summary>
public static class TabStyle
{
    public static IBrush? Accent(string? colour) => Color.TryParse(colour, out var c) ? new SolidColorBrush(c) : null;

    public static IBrush? Tint(string? colour, double opacity) => Color.TryParse(colour, out var c) ? new SolidColorBrush(c, opacity) : null;

    /// <summary>The tab's icon in its colour (recolourable icons), or null for the plain folder icon.</summary>
    public static Bitmap? Icon(RepoSettings? style) =>
        style?.TabIcon is { } id ? IconLibrary.Get(id, style.TabColor) : null;
}

// The tab's colour and icon, and the recent-repository tiles an empty tab shows.
public partial class RepositoryViewModel
{
    /// <summary>The repository's settings key: its main working directory (shared by all its worktrees' tabs).</summary>
    private string? TabStyleKey => _state?.MainWorkingDirectory ?? PendingPath;

    private RepoSettings? TabSettings => TabStyleKey is { } key ? _settings.FindRepository(key) : null;

    public IBrush? TabAccent => TabStyle.Accent(TabSettings?.TabColor);
    public IBrush? TabTint => TabStyle.Tint(TabSettings?.TabColor, 0.16);
    public IBrush? TabTintStrong => TabStyle.Tint(TabSettings?.TabColor, 0.32);
    public Bitmap? TabIcon => TabStyle.Icon(TabSettings);
    public bool HasTabIcon => TabIcon is not null;
    public bool HasNoTabIcon => TabIcon is null;

    private void OnTabStyleChanged(string mainRoot)
    {
        if (TabStyleKey is { } key && WorktreeService.SamePath(key, mainRoot)) RefreshTabStyle();
        if (ShowEmptyState) RefreshRecent();
    }

    private void RefreshTabStyle()
    {
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(TabAccent));
        OnPropertyChanged(nameof(TabTint));
        OnPropertyChanged(nameof(TabTintStrong));
        OnPropertyChanged(nameof(TabIcon));
        OnPropertyChanged(nameof(HasTabIcon));
        OnPropertyChanged(nameof(HasNoTabIcon));
    }

    /// <summary>Renames this repository's tabs (empty: back to the folder's name).</summary>
    [RelayCommand]
    private Task RenameTabAsync() => TabStyleKey is { } key ? RenameTabForAsync(key, RepositoryName) : Task.CompletedTask;

    private async Task RenameTabForAsync(string mainRoot, string folderName)
    {
        if (Dialogs is null) return;
        var name = FormField.TextBox("Name", _settings.FindRepository(mainRoot)?.TabName ?? folderName, placeholder: folderName);
        var spec = new FormSpec("Rename tab",
            $"The name this repository's tabs show. Leave it empty to use the folder's name ({folderName}).", "Rename", [name]);
        if (!await Dialogs.ShowFormAsync(spec)) return;
        var text = name.Text.Trim();
        _settings.SetTabName(mainRoot, text == folderName ? null : text);
    }

    /// <summary>Opens the colour and icon picker for this repository's tabs.</summary>
    [RelayCommand]
    private Task EditTabStyleAsync() => TabStyleKey is { } key ? EditTabStyleForAsync(key, RepositoryName) : Task.CompletedTask;

    private async Task EditTabStyleForAsync(string mainRoot, string name)
    {
        if (Dialogs is null) return;
        var current = _settings.FindRepository(mainRoot);
        var vm = new TabStyleViewModel(current?.TabName ?? name, current?.TabColor, current?.TabIcon);
        if (!await Dialogs.ShowTabStyleAsync(vm)) return;
        _settings.SetTabStyle(mainRoot, vm.Colour, vm.IconId);
    }

    // ------------------------------------------------------------------ recent repositories

    public ObservableCollection<RecentRepositoryItem> RecentItems { get; } = [];

    public bool HasRecent => RecentItems.Count > 0;

    [ObservableProperty]
    public partial string RecentFilter { get; set; } = "";

    partial void OnRecentFilterChanged(string value) => RefreshRecent();

    public bool ShowRecentFilter => _settings.Recent().Count > 12;

    /// <summary>Rebuilds the tiles from the recent list (most recent first), filtered by name or path.</summary>
    public void RefreshRecent()
    {
        var filter = RecentFilter.Trim();
        RecentItems.Clear();
        foreach (var r in _settings.Recent()
                     .Where(r => filter.Length == 0 || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                 || r.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(r => r.LastOpened))
            RecentItems.Add(new RecentRepositoryItem(r, _settings.FindRepository(r.Path)));
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(ShowRecentFilter));
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && ShowEmptyState) RefreshRecent();
    }

    [RelayCommand]
    private Task OpenRecentAsync(RecentRepositoryItem item)
    {
        if (!item.Exists) return Task.CompletedTask;
        return OpenInTabHandler?.Invoke(item.Path) ?? LoadAsync(item.Path);
    }

    [RelayCommand]
    private void RemoveRecent(RecentRepositoryItem item)
    {
        _settings.RemoveRecent(item.Path);
        RecentItems.Remove(item);
        OnPropertyChanged(nameof(HasRecent));
    }

    /// <summary>Right-click on a recent repository tile.</summary>
    public IReadOnlyList<MenuAction> ActionsForRecent(RecentRepositoryItem item) =>
    [
        new MenuAction("Open", OpenRecentCommand, item, IsEnabled: item.Exists, Icon: MenuIcons.Open),
        new MenuAction("Reveal in Explorer", RevealCommand, item.Path, IsEnabled: item.Exists, Icon: MenuIcons.Folder),
        new MenuAction("Rename…", new AsyncRelayCommand(() => RenameTabForAsync(item.Path, item.Recent.Name))),
        new MenuAction("Colour and icon…", new AsyncRelayCommand(() => EditTabStyleForAsync(item.Path, item.Recent.Name))),
        MenuAction.Separator,
        new MenuAction("Copy path", CopyCommand, item.Path, Icon: MenuIcons.Copy),
        new MenuAction("Remove from recent", RemoveRecentCommand, item, Icon: MenuIcons.Delete),
    ];
}
