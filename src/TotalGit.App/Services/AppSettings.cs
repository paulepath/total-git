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

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TotalGit");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    public string? LastRepository { get; set; }

    /// <summary>Optional explicit path to VS Code (Code.exe or code.cmd).</summary>
    public string? VsCodePath { get; set; }

    public double SidebarWidth { get; set; } = 240;
    public double DetailsWidth { get; set; } = 380;

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
