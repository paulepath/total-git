using System.Text.Json;

namespace TotalGit.App.Services;

/// <summary>Per-repository preferences, keyed by the main worktree path.</summary>
public sealed class RepoSettings
{
    public List<string>? LocalFilePatterns { get; set; }
    public bool LinkNodeModules { get; set; } = true;
    public bool CopyLocalFiles { get; set; } = true;

    /// <summary>The Jira site ticket keys in pull request titles link to, e.g. https://example.atlassian.net.</summary>
    public string? JiraUrl { get; set; }

    /// <summary>The colour of this repository's tabs (#RRGGBB), or null for none.</summary>
    public string? TabColor { get; set; }

    /// <summary>The icon of this repository's tabs (an icon library id), or null for the folder icon.</summary>
    public string? TabIcon { get; set; }

    /// <summary>The name its tabs show instead of the folder's name, or null for the folder's name.</summary>
    public string? TabName { get; set; }

    /// <summary>The graph shows only the current branch's line down to the main branch.</summary>
    public bool CurrentBranchOnly { get; set; }

    /// <summary>The graph folds runs of consecutive commits on one line into one row.</summary>
    public bool FoldRuns { get; set; } = true;
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Settings and avatar cache; TOTALGIT_DATA_DIR overrides it (e.g. for demos or testing).</summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("TOTALGIT_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TotalGit");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Only read to seed <see cref="OpenTabs"/> for settings from before tabs existed.</summary>
    public string? LastRepository { get; set; }

    /// <summary>Folders open in tabs, restored on start-up (null until tabs have been saved once).</summary>
    public List<string>? OpenTabs { get; set; }
    public int SelectedTab { get; set; }

    /// <summary>Optional explicit path to VS Code (Code.exe or code.cmd).</summary>
    public string? VsCodePath { get; set; }

    /// <summary>The solution last opened in Visual Studio, by worktree folder (when it has several).</summary>
    public Dictionary<string, string> LastSolutions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whole-UI zoom (1 = 100%), changed with Ctrl + wheel or Ctrl +/-.</summary>
    public double Zoom { get; set; } = 1.0;

    public double SidebarWidth { get; set; } = 240;

    /// <summary>The sidebar stays open; unpinned it folds to a rail and opens while pointed at.</summary>
    public bool SidebarPinned { get; set; } = true;
    public double DetailsWidth { get; set; } = 380;

    // Commit graph columns; a null graph width means sized to the lanes.
    public double RefColumnWidth { get; set; } = 170;
    public double? GraphColumnWidth { get; set; }
    public double AuthorColumnWidth { get; set; } = 160;
    public double DateColumnWidth { get; set; } = 140;

    /// <summary>"Inline" or "Split".</summary>
    public string DiffMode { get; set; } = "Inline";

    /// <summary>Diffs show the whole file (changes marked in place), not just the changes and a few lines round them.</summary>
    public bool DiffWholeFile { get; set; }

    /// <summary>The pull request review window's last size (and whether it was maximised).</summary>
    public double ReviewWindowWidth { get; set; } = 1500;
    public double ReviewWindowHeight { get; set; } = 950;
    public bool ReviewWindowMaximized { get; set; }

    /// <summary>The review window's file list leaves out test files (to look at later).</summary>
    public bool ReviewHideTests { get; set; }

    /// <summary>The preselected option for uncommitted changes when checking out a branch or commit.</summary>
    public TotalGit.Core.Git.LocalChanges CheckoutLocalChanges { get; set; }

    /// <summary>Staging lists grouped by folder (otherwise a flat list).</summary>
    public bool StagingTree { get; set; } = true;

    /// <summary>A commit's (or another worktree's) changed files grouped by folder (otherwise a flat list).</summary>
    public bool ChangedFilesTree { get; set; } = true;

    /// <summary>How branches are grouped and which icons they get, in order; null for the built-in rules.</summary>
    public List<TotalGit.Core.Git.BranchRule>? BranchRules { get; set; }

    /// <summary>Makes <see cref="BranchRules"/> the rules the whole app uses.</summary>
    public void ApplyBranchRules() =>
        TotalGit.Core.Git.BranchRuleSet.Current = new TotalGit.Core.Git.BranchRuleSet(BranchRules ?? TotalGit.Core.Git.BranchRuleSet.Defaults());

    /// <summary>The sidebar lists the repository's CI workflow runs (GitHub Actions).</summary>
    public bool ShowWorkflows { get; set; } = true;

    /// <summary>The merge tool shows the common ancestor next to the two sides (when git has one).</summary>
    public bool MergeShowBase { get; set; } = true;

    public Dictionary<string, RepoSettings> Repositories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Repositories opened before, most recently opened first (shown as tiles on a new tab).</summary>
    public List<RecentRepository>? RecentRepositories { get; set; }

    private const int MaxRecent = 30;

    /// <summary>The recent repositories; the first time, made from the repositories and tabs already known.</summary>
    public IReadOnlyList<RecentRepository> Recent()
    {
        if (RecentRepositories is null)
        {
            RecentRepositories = Repositories.Keys
                .Concat(OpenTabs ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => new RecentRepository(p, Path.GetFileName(Path.TrimEndingDirectorySeparator(p)), DateTimeOffset.MinValue))
                .Take(MaxRecent)
                .ToList();
        }
        return RecentRepositories;
    }

    /// <summary>Records a repository as just opened (moving it to the front).</summary>
    public void TouchRecent(string path, string name)
    {
        var list = Recent().Where(r => !string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Insert(0, new RecentRepository(path, name, DateTimeOffset.Now));
        RecentRepositories = list.Take(MaxRecent).ToList();
        Save();
    }

    public void RemoveRecent(string path)
    {
        RecentRepositories = Recent().Where(r => !string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
        Save();
    }

    /// <summary>Raised when a repository's tab colour or icon changes, so every tab of it (and the tiles) update.</summary>
    public event Action<string>? TabStyleChanged;

    public void SetTabStyle(string mainRoot, string? colour, string? icon)
    {
        var repo = ForRepository(mainRoot);
        repo.TabColor = colour;
        repo.TabIcon = icon;
        Save();
        TabStyleChanged?.Invoke(mainRoot);
    }

    public void SetTabName(string mainRoot, string? name)
    {
        ForRepository(mainRoot).TabName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Save();
        TabStyleChanged?.Invoke(mainRoot);
    }

    /// <summary>A repository's saved settings, if it has any (without creating them).</summary>
    public RepoSettings? FindRepository(string mainRoot) => Repositories.GetValueOrDefault(mainRoot);

    public RepoSettings ForRepository(string mainRoot)
    {
        if (!Repositories.TryGetValue(mainRoot, out var s)) Repositories[mainRoot] = s = new RepoSettings();
        return s;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
                loaded.Repositories = new(loaded.Repositories, StringComparer.OrdinalIgnoreCase);
                return loaded;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (IOException) { }
    }
}

/// <summary>A repository opened before: its folder (the main working directory), name and when it was last opened.</summary>
public sealed record RecentRepository(string Path, string Name, DateTimeOffset LastOpened);
