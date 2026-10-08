using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TotalGit.Core.Git;

/// <summary>Path-scoped recovery of both working bytes and index versions, including unborn repositories.</summary>
public sealed class DiscardBackup
{
    public const int KeepCount = 20;
    public static readonly TimeSpan KeepAge = TimeSpan.FromDays(7);
    private const string ManifestName = "backup.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Id { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public List<SavedPath> Paths { get; set; } = [];
    public Dictionary<string, string> Before { get; set; } = [];
    public Dictionary<string, string>? After { get; set; }

    public sealed class SavedPath
    {
        public string Path { get; set; } = "";
        public string? File { get; set; }
        public string? Link { get; set; }
        public FileAttributes Attributes { get; set; }
        public List<SavedEntry> Index { get; set; } = [];
    }

    public sealed record SavedEntry(string Mode, string Sha, string Stage, string? File);

    /// <param name="paths">Literal paths and folders, or null for everything with uncommitted changes.</param>
    /// <param name="includeUntracked">With <paramref name="paths"/> null: back up untracked files too (they're about to be deleted).</param>
    public static async Task<DiscardBackup?> CreateAsync(string worktree, IReadOnlyList<string>? paths, bool includeUntracked = true)
    {
        var entries = await IndexAsync(worktree);
        List<string> names;
        if (paths is null)
        {
            // Only what has changed: copying the whole tree would take minutes in a large repository.
            var status = await GitCli.RunAsync(worktree, "status", "--porcelain=v1", "-z", "--no-renames",
                includeUntracked ? "--untracked-files=all" : "--untracked-files=no");
            names = status.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(e => e.Length > 3 && !e.StartsWith("!!", StringComparison.Ordinal)).Select(e => e[3..])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        }
        else
        {
            var others = (await GitCli.RunAsync(worktree, "ls-files", "--others", "--exclude-standard", "-z")).StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            bool Wanted(string p) => paths.Any(t => p == t || p.StartsWith(t.TrimEnd('/') + "/", StringComparison.Ordinal));
            names = entries.Keys.Concat(others).Where(Wanted).Concat(paths).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        }
        if (names.Count == 0) return null;
        var root = await RootAsync(worktree);
        Directory.CreateDirectory(root);
        Prune(root);
        var backup = new DiscardBackup { Id = Guid.NewGuid().ToString("N"), Created = DateTimeOffset.UtcNow };
        var folder = Path.Combine(root, backup.Id);
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var name in names)
            {
                var source = SafePath(worktree, name);
                var info = new FileInfo(source);
                // A submodule keeps only its recorded commit (below); git doesn't discard inside it. Any other folder
                // (a nested repository) is something the discard leaves alone.
                if (Directory.Exists(source) && info.LinkTarget is null && !IsSubmodule(entries, name)) continue;
                var saved = new SavedPath { Path = name, Link = info.LinkTarget };
                if (saved.Link is null && info.Exists)
                {
                    saved.File = $"{backup.Paths.Count}-work";
                    saved.Attributes = info.Attributes;
                    File.Copy(source, Path.Combine(folder, saved.File));
                    File.SetAttributes(Path.Combine(folder, saved.File), FileAttributes.Normal);
                }
                if (entries.TryGetValue(name, out var index))
                {
                    foreach (var entry in index)
                    {
                        var file = entry.Mode == "160000" ? null : $"{backup.Paths.Count}-index-{entry.Stage}";
                        if (file is not null) await GitCli.SaveBlobAsync(worktree, entry.Sha, Path.Combine(folder, file));
                        saved.Index.Add(entry with { File = file });
                    }
                }
                backup.Paths.Add(saved);
            }
            backup.Before = await backup.FingerprintsAsync(worktree);
            backup.Save(folder);
            return backup;
        }
        catch
        {
            Directory.Delete(folder, recursive: true);
            throw; // A failed backup must stop the discard.
        }
    }

    public async Task CompleteAsync(string worktree)
    {
        After = await FingerprintsAsync(worktree);
        Paths.RemoveAll(p => Before.GetValueOrDefault(p.Path) == After.GetValueOrDefault(p.Path));
        After = Paths.ToDictionary(p => p.Path, p => After[p.Path]);
        var root = await RootAsync(worktree);
        Save(Path.Combine(root, Id));
        Prune(root);
    }

    public static async Task<DiscardBackup?> LatestAsync(string worktree)
    {
        var root = await RootAsync(worktree);
        if (!Directory.Exists(root)) return null;
        Prune(root);
        return Directory.EnumerateDirectories(root).Select(Read).Where(b => b is { After: not null, Paths.Count: > 0 })
            .OrderByDescending(b => b!.Created).FirstOrDefault();
    }

    public async Task UndoAsync(string worktree)
    {
        var folder = Path.Combine(await RootAsync(worktree), Id);
        if (!Directory.Exists(folder) || After is null)
            throw new InvalidOperationException("This discard backup is no longer available.");
        var current = await FingerprintsAsync(worktree);
        if (After.Any(p => current.GetValueOrDefault(p.Key) != p.Value))
            throw new InvalidOperationException("Files or staged changes affected by this discard have changed since. Undo was stopped to protect your newer changes.");

        // Prepare every index blob before changing any working files.
        var index = new StringBuilder();
        var zero = new string('0', (await GitCli.RunAsync(worktree, "hash-object", "--stdin")).StdOut.Trim().Length);
        foreach (var path in Paths)
        {
            index.Append("0 ").Append(zero).Append('\t').Append(path.Path).Append('\0');
            foreach (var entry in path.Index)
            {
                var sha = entry.File is null ? entry.Sha
                    : (await GitCli.RunAsync(worktree, "hash-object", "-w", "--no-filters", "--", Path.Combine(folder, entry.File))).StdOut.Trim();
                index.Append(entry.Mode).Append(' ').Append(sha).Append(' ').Append(entry.Stage).Append('\t').Append(path.Path).Append('\0');
            }
        }
        foreach (var saved in Paths)
        {
            var target = SafePath(worktree, saved.Path);
            var info = new FileInfo(target);
            if (info.Exists || info.LinkTarget is not null)
            {
                if (info.LinkTarget is null) File.SetAttributes(target, FileAttributes.Normal);
                File.Delete(target);
            }
            if (saved.File is not null || saved.Link is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (saved.Link is not null) File.CreateSymbolicLink(target, saved.Link);
                else
                {
                    File.Copy(Path.Combine(folder, saved.File!), target);
                    File.SetAttributes(target, saved.Attributes);
                }
            }
        }
        await GitCli.RunAsync(worktree, ["update-index", "-z", "--index-info"], stdin: index.ToString());
        // Retain the files for manual recovery, but don't offer a repeated undo.
        After = null;
        Save(folder);
    }

    public static void Prune(string root, DateTimeOffset? now = null)
    {
        var cutoff = (now ?? DateTimeOffset.UtcNow) - KeepAge;
        var backups = Directory.EnumerateDirectories(root).Select(p => (Folder: p, Backup: Read(p)))
            .Where(b => b.Backup is not null).OrderByDescending(b => b.Backup!.Created).ToList();
        foreach (var (folder, backup) in backups.Where((b, i) => i >= KeepCount || b.Backup!.Created < cutoff))
        {
            // Only our GUID-named children of the resolved backup root are eligible for deletion.
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _)) continue;
            Directory.Delete(folder, recursive: true);
        }
    }

    private static DiscardBackup? Read(string folder)
    {
        var file = Path.Combine(folder, ManifestName);
        // A damaged manifest (e.g. a crash mid-write) just isn't offered.
        try { return File.Exists(file) ? JsonSerializer.Deserialize<DiscardBackup>(File.ReadAllText(file)) : null; }
        catch (JsonException) { return null; }
    }

    private void Save(string folder)
    {
        var file = Path.Combine(folder, ManifestName);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, JsonOptions));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    private static async Task<string> RootAsync(string worktree) => Path.Combine(
        (await GitCli.RunAsync(worktree, "rev-parse", "--absolute-git-dir")).StdOut.Trim(), "totalgit", "discarded");

    private static string SafePath(string worktree, string relative)
    {
        var root = Path.GetFullPath(worktree) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(worktree, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Backup path is outside the worktree.");
        // Never follow a parent symlink into another folder when copying or restoring.
        for (var parent = Path.GetDirectoryName(path); parent is not null && parent.Length >= root.Length; parent = Path.GetDirectoryName(parent))
            if (new DirectoryInfo(parent).LinkTarget is not null) throw new IOException("Backup path has a linked parent folder.");
        return path;
    }

    private async Task<Dictionary<string, string>> FingerprintsAsync(string worktree)
    {
        var entries = await IndexAsync(worktree);
        var result = new Dictionary<string, string>();
        foreach (var path in Paths)
        {
            var file = SafePath(worktree, path.Path);
            var info = new FileInfo(file);
            var content = Directory.Exists(file) && info.LinkTarget is null ? "folder" : info.LinkTarget is { } link ? "link:" + link : info.Exists ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) + ":" + info.Attributes : "missing";
            var index = entries.TryGetValue(path.Path, out var list) ? string.Join(';', list.Select(e => $"{e.Mode} {e.Sha} {e.Stage}")) : "";
            result[path.Path] = content + "|" + index;
        }
        return result;
    }

    private static bool IsSubmodule(Dictionary<string, List<SavedEntry>> entries, string path) =>
        entries.TryGetValue(path, out var list) && list.Any(e => e.Mode == "160000");

    private static async Task<Dictionary<string, List<SavedEntry>>> IndexAsync(string worktree)
    {
        var result = new Dictionary<string, List<SavedEntry>>(StringComparer.Ordinal);
        var text = (await GitCli.RunAsync(worktree, "ls-files", "--stage", "-z")).StdOut;
        foreach (var row in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = row.IndexOf('\t');
            var fields = row[..tab].Split(' ');
            var path = row[(tab + 1)..];
            if (!result.TryGetValue(path, out var list)) result[path] = list = [];
            list.Add(new(fields[0], fields[1], fields[2], null));
        }
        return result;
    }
}
