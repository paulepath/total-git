using System.Text.Json;

namespace TotalGit.App.Services;

/// <summary>Per-repository preferences, keyed by the main worktree path.</summary>
public sealed class RepoSettings
{
    public List<string>? LocalFilePatterns { get; set; }
    public bool LinkNodeModules { get; set; } = true;
    public bool CopyLocalFiles { get; set; } = true;
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

    public double SidebarWidth { get; set; } = 240;
    public double DetailsWidth { get; set; } = 380;

    // Commit graph columns; a null graph width means sized to the lanes.
    public double RefColumnWidth { get; set; } = 170;
    public double? GraphColumnWidth { get; set; }
    public double AuthorColumnWidth { get; set; } = 160;
    public double DateColumnWidth { get; set; } = 140;

    /// <summary>"Inline" or "Split".</summary>
    public string DiffMode { get; set; } = "Inline";

    /// <summary>The preselected option for uncommitted changes when checking out a branch or commit.</summary>
    public TotalGit.Core.Git.LocalChanges CheckoutLocalChanges { get; set; }

    /// <summary>Staging lists grouped by folder (otherwise a flat list).</summary>
    public bool StagingTree { get; set; } = true;

    public Dictionary<string, RepoSettings> Repositories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
