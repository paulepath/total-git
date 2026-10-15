using System.Text;

namespace TotalGit.Core.Git;

/// <summary>
/// How <c>TotalGit --merge</c> ends, as its exit code (git mergetool, or another app, waits for it).
/// </summary>
public enum MergeExit
{
    /// <summary>The file was written with every conflict resolved (no markers left).</summary>
    Saved = 0,

    /// <summary>Closed without saving; the file is untouched.</summary>
    Cancelled = 1,

    /// <summary>Bad arguments, no such file, or not in a git repository.</summary>
    Usage = 2,

    /// <summary>A conflict the merge window can't take: deleted on one side, binary, not UTF-8, or not conflicted.</summary>
    Unsupported = 3,
}

/// <summary>
/// The command line of the stand-alone merge window: <c>--merge &lt;file&gt;</c>. Further arguments (git mergetool's
/// $BASE, $LOCAL, $REMOTE) are ignored: the sides come from the index.
/// </summary>
public sealed record MergeCommandLine(string File)
{
    public const string Switch = "--merge";

    public static bool IsMerge(IReadOnlyList<string> args) => args.Count > 0 && args[0] == Switch;

    /// <summary>Null when there's no file.</summary>
    public static MergeCommandLine? Parse(IReadOnlyList<string> args) =>
        IsMerge(args) && args.Count > 1 && !string.IsNullOrWhiteSpace(args[1]) ? new MergeCommandLine(args[1]) : null;
}

/// <summary>A conflicted file the merge window can open: its worktree, git directory and path in the repository.</summary>
public sealed record MergeTarget(string Worktree, string GitDirectory, string Path);

/// <param name="Target">Set when the file can be merged; otherwise <see cref="Exit"/> and <see cref="Reason"/> say why not.</param>
public sealed record MergeFileCheck(MergeTarget? Target, MergeExit Exit, string Reason)
{
    private static MergeFileCheck Fail(MergeExit exit, string reason) => new(null, exit, reason);

    /// <summary>
    /// Checks that <paramref name="fullPath"/> is a conflicted text file in a git repository, with both sides
    /// present in the index and conflict markers in the working tree. Changes nothing.
    /// </summary>
    public static async Task<MergeFileCheck> RunAsync(string fullPath)
    {
        var folder = Path.GetDirectoryName(fullPath);
        var name = Path.GetFileName(fullPath);
        if (folder is null || name.Length == 0 || !Directory.Exists(folder))
            return Fail(MergeExit.Usage, $"No such file: {fullPath}");

        var repo = await GitCli.RunAsync(folder, ["rev-parse", "--show-toplevel", "--absolute-git-dir"], throwOnError: false);
        var lines = repo.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (repo.ExitCode != 0 || lines.Length < 2)
            return Fail(MergeExit.Usage, $"Not in a git repository: {fullPath}");
        var (worktree, gitDir) = (Path.GetFullPath(lines[0]), Path.GetFullPath(lines[1]));

        // The index's stages for the file: 1 base, 2 ours, 3 theirs ("<mode> <sha> <stage>\t<path>", paths from the root).
        var listed = await GitCli.RunAsync(folder, ["ls-files", "-u", "-z", "--full-name", "--", ":(literal)" + name], throwOnError: false);
        var stages = listed.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Split('\t', 2))
            .Where(p => p.Length == 2 && p[0].Split(' ') is [_, _, var s] && s.Length == 1)
            .Select(p => (Stage: p[0][^1], Path: p[1]))
            .ToList();
        var exists = File.Exists(fullPath);
        if (stages.Count == 0)
            return exists
                ? Fail(MergeExit.Unsupported, $"{name} isn't conflicted (git has no unmerged versions of it).")
                : Fail(MergeExit.Usage, $"No such file: {fullPath}");
        if (!stages.Any(s => s.Stage == '2') || !stages.Any(s => s.Stage == '3'))
            return Fail(MergeExit.Unsupported, $"{name} was deleted on one side. Keep or delete it with git instead.");
        if (!exists)
            return Fail(MergeExit.Usage, $"No such file: {fullPath}");

        var bytes = await File.ReadAllBytesAsync(fullPath);
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
            return Fail(MergeExit.Unsupported, $"{name} is a binary file. Take one side's version instead.");
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, bom, bytes.Length - bom);
        }
        catch (DecoderFallbackException)
        {
            return Fail(MergeExit.Unsupported, $"{name} isn't UTF-8 text, which the merge window needs.");
        }
        if (!ConflictFile.HasMarkers(text))
            return Fail(MergeExit.Unsupported, $"{name} has no conflict markers left.");

        return new MergeFileCheck(new MergeTarget(worktree, gitDir, stages[0].Path), MergeExit.Saved, "");
    }
}
