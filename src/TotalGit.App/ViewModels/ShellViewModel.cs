using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Avatars;
using TotalGit.Core.Hosting;
using TotalGit.Core.Worktrees;

namespace TotalGit.App.ViewModels;

/// <summary>The window: repository tabs (restored on start-up) and app-wide state such as updates.</summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly AvatarCache _avatars;
    private readonly UpdateService? _updates;
    private readonly IPullRequestProviderFactory? _pullRequests;
    private readonly IWorkflowProviderFactory? _workflows;
    private DispatcherTimer? _updateTimer;
    private bool _restoring;

    public ShellViewModel(AvatarService avatars, AppSettings settings, UpdateService? updates = null,
        IPullRequestProviderFactory? pullRequests = null,
        IWorkflowProviderFactory? workflows = null)
    {
        _settings = settings;
        _avatars = new AvatarCache(avatars);
        _updates = updates;
        _pullRequests = pullRequests;
        _workflows = workflows;
        AppVersion = updates?.CurrentVersion ?? "dev";
        StartUpdateChecks();
    }

    public ObservableCollection<RepositoryViewModel> Tabs { get; } = [];
    public AppSettings Settings => _settings;
    public string AppVersion { get; }

    /// <summary>Set by the view; passed on to every tab.</summary>
    public IDialogService? Dialogs
    {
        get;
        set
        {
            field = value;
            foreach (var tab in Tabs) tab.Dialogs = value;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial RepositoryViewModel? SelectedTab { get; set; }

    public string WindowTitle => $"Total Git {AppVersion} - {SelectedTab?.TabTitle ?? "No repository"}";

    /// <summary>Version of a downloaded update waiting for a restart.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    public partial string? UpdateVersion { get; set; }

    public bool HasUpdate => UpdateVersion is not null;

    partial void OnSelectedTabChanged(RepositoryViewModel? oldValue, RepositoryViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            newValue.EnsureLoaded();
        }
        SaveTabs();
    }

    // ------------------------------------------------------------------ tabs

    /// <summary>Reopens the tabs from last time, then opens <paramref name="startPath"/> (a command-line argument) if given.</summary>
    public void RestoreTabs(string? startPath)
    {
        _restoring = true;
        var paths = _settings.OpenTabs ?? (_settings.LastRepository is { } last ? [last] : []);
        foreach (var path in paths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            AddTab(NewTabViewModel(path));
        if (Tabs.Count == 0) AddTab(NewTabViewModel());

        var index = Math.Clamp(_settings.SelectedTab, 0, Tabs.Count - 1);
        _restoring = false;
        SelectedTab = Tabs[index];

        if (!string.IsNullOrEmpty(startPath) && Directory.Exists(startPath)) _ = OpenPathAsync(startPath);
    }

    /// <summary>Shows <paramref name="path"/>: switches to its tab if open, else uses an empty tab or adds one.</summary>
    public async Task OpenPathAsync(string path)
    {
        var existing = Tabs.FirstOrDefault(t => t.TabPath is { } p && WorktreeService.SamePath(p, path));
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        var tab = SelectedTab is { IsEmptyTab: true } empty ? empty : AddTab(NewTabViewModel());
        SelectedTab = tab;
        await tab.LoadAsync(path);
    }

    [RelayCommand]
    private async Task OpenRepositoryAsync()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.PickFolderAsync();
        if (path is not null) await OpenPathAsync(path);
    }

    private RepositoryViewModel NewTabViewModel(string? path = null) =>
        new(_avatars, _settings, path) { PullRequestProviders = _pullRequests, WorkflowProviders = _workflows };

    [RelayCommand]
    private void NewTab() => SelectedTab = AddTab(NewTabViewModel());

    [RelayCommand]
    private void CloseTab(RepositoryViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null) return;

        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        if (Tabs.Count == 1)
        {
            // Keep one tab: closing the last one leaves an empty "New tab".
            AddTab(NewTabViewModel());
        }

        var wasSelected = tab == SelectedTab;
        Tabs.RemoveAt(index);
        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.Loaded -= SaveTabs;
        tab.Dispose();
        if (wasSelected) SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        SaveTabs();
    }

    [RelayCommand]
    private void CloseOtherTabs(RepositoryViewModel? keep)
    {
        keep ??= SelectedTab;
        if (keep is null) return;
        foreach (var tab in Tabs.Where(t => t != keep).ToList()) CloseTab(tab);
        SelectedTab = keep;
    }

    /// <summary>Right-click on a tab.</summary>
    public IReadOnlyList<MenuAction> ActionsForTab(RepositoryViewModel tab)
    {
        var actions = new List<MenuAction>();
        if (tab.TabPath is { } path)
        {
            actions.Add(new MenuAction("Rename tab…", tab.RenameTabCommand));
            actions.Add(new MenuAction("Colour and icon…", tab.EditTabStyleCommand));
            actions.Add(MenuAction.Separator);
            actions.Add(new MenuAction("Copy path", tab.CopyCommand, path, Icon: MenuIcons.Copy));
            actions.Add(new MenuAction("Reveal in Explorer", tab.RevealCommand, path, Icon: MenuIcons.Folder));
            actions.Add(MenuAction.Separator);
        }
        actions.Add(new MenuAction("Close tab", CloseTabCommand, tab, Icon: MenuIcons.Delete));
        actions.Add(new MenuAction("Close other tabs", CloseOtherTabsCommand, tab, IsEnabled: Tabs.Count > 1));
        return actions;
    }

    [RelayCommand]
    private void NextTab() => MoveSelection(1);

    [RelayCommand]
    private void PreviousTab() => MoveSelection(-1);

    private void MoveSelection(int delta)
    {
        if (Tabs.Count < 2 || SelectedTab is null) return;
        var index = (Tabs.IndexOf(SelectedTab) + delta + Tabs.Count) % Tabs.Count;
        SelectedTab = Tabs[index];
    }

    private RepositoryViewModel AddTab(RepositoryViewModel tab)
    {
        tab.Dialogs = Dialogs;
        tab.OpenRepositoryHandler = OpenRepositoryAsync;
        tab.OpenInTabHandler = OpenPathAsync;
        tab.PropertyChanged += OnTabPropertyChanged;
        tab.Loaded += SaveTabs;
        Tabs.Add(tab);
        return tab;
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == SelectedTab && e.PropertyName == nameof(RepositoryViewModel.TabTitle))
            OnPropertyChanged(nameof(WindowTitle));

        // The diff mode is a single app-wide preference.
        if (e.PropertyName == nameof(RepositoryViewModel.DiffMode) && sender is RepositoryViewModel source)
        {
            foreach (var tab in Tabs.Where(t => t != source)) tab.DiffMode = source.DiffMode;
        }
        // So is showing whole files.
        if (e.PropertyName == nameof(RepositoryViewModel.WholeFileDiff) && sender is RepositoryViewModel changed)
        {
            foreach (var tab in Tabs.Where(t => t != changed)) tab.WholeFileDiff = changed.WholeFileDiff;
        }
    }

    private void SaveTabs()
    {
        if (_restoring) return;
        _settings.OpenTabs = Tabs.Select(t => t.TabPath).OfType<string>().ToList();
        var selected = SelectedTab?.TabPath;
        _settings.SelectedTab = selected is null ? 0 : Math.Max(0, _settings.OpenTabs.FindIndex(p => p == selected));
        _settings.Save();
    }

    // ------------------------------------------------------------------ updates

    private void StartUpdateChecks()
    {
        if (_updates is not { IsInstalled: true } updates) return;
        updates.UpdateReady += version => Dispatcher.UIThread.Post(() =>
        {
            UpdateVersion = version;
            SelectedTab?.ShowBanner(new Banner($"Total Git {version} is ready.", false,
                [new MenuAction("Restart to update", RestartToUpdateCommand)]));
        });

        // First check shortly after start-up, then every few hours.
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(4);
            _ = updates.CheckAndDownloadAsync();
        };
        _updateTimer.Start();
    }

    [RelayCommand]
    private void RestartToUpdate() => _updates?.RestartToUpdate();
}
