using System.Text;
using System.Text.RegularExpressions;
using TotalGit.Core.Git;

namespace TotalGit.Core.Hosting;

/// <summary>
/// A change suggested in a line comment (a <c>```suggestion</c> block): the lines of the pull request's head it
/// replaces, and what with (no lines at all deletes them).
/// </summary>
public sealed record SuggestedChange(string Path, int StartLine, int EndLine, IReadOnlyList<string> Replacement, PrUser Author);

/// <summary>
/// Suggested changes: finding them in comments, and committing them onto a pull request's branch the way GitHub's
/// "Commit suggestion" does (GitHub has no API for that, so the commit is made here and pushed).
/// </summary>
public static partial class Suggestions
{
    [GeneratedRegex(@"^ {0,3}(?<fence>`{3,}|~{3,})[ \t]*suggestion[ \t]*$")]
    private static partial Regex Opening();

    /// <summary>The <c>```suggestion</c> blocks in a comment, in order, as their lines (without the fences).</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string body)
    {
        var result = new List<IReadOnlyList<string>>();
        var lines = body.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var open = Opening().Match(lines[i]);
            if (!open.Success) continue;
            var fence = open.Groups["fence"].Value;
            var block = new List<string>();
            var j = i + 1;
            // An unclosed fence runs to the end of the comment, as Markdown has it.
            for (; j < lines.Length; j++)
            {
                var trimmed = lines[j].TrimStart(' ');
                if (lines[j].Length - trimmed.Length <= 3 && trimmed.TrimEnd().Length >= fence.Length
                    && trimmed.TrimEnd().All(c => c == fence[0]))
                    break;
                block.Add(lines[j]);
            }
            result.Add(block);
            i = j;
        }
        return result;
    }

    /// <summary>
    /// The lines of a comment's diff hunk that its suggestion replaces: the last <paramref name="count"/> lines of the
    /// new side (a hunk ends at the commented line). Null when the hunk is too short.
    /// </summary>
    public static IReadOnlyList<string>? ReplacedLines(string? diffHunk, int count)
    {
        if (diffHunk is null || count <= 0) return null;
        var newSide = diffHunk.Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.StartsWith("@@", StringComparison.Ordinal) && !l.StartsWith('-') && !l.StartsWith('\\'))
            .Select(l => l.Length > 0 ? l[1..] : l)
            .ToList();
        return newSide.Count < count ? null : newSide[^count..];
    }

    /// <summary>
    /// Replaces lines <paramref name="start"/> to <paramref name="end"/> (1-based, inclusive) of a file with
    /// <paramref name="replacement"/>, keeping the file's line endings and whether its last line ends in one.
    /// </summary>
    public static string Apply(string text, int start, int end, IReadOnlyList<string> replacement)
    {
        // Each line keeps its own ending, so a file with mixed endings stays as it was outside the change.
        var lines = Regex.Split(text, "(?<=\n)").ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (start < 1 || end < start || end > lines.Count)
            throw new InvalidOperationException($"Lines {start}–{end} aren't in the file any more.");

        var newline = lines[start - 1].EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
            : lines[start - 1].EndsWith('\n') ? "\n"
            : text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        // The last replaced line keeps its ending: none when it was the file's unterminated last line.
        var lastEnding = lines[end - 1].EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : lines[end - 1].EndsWith('\n') ? "\n" : "";

        var result = new StringBuilder();
        foreach (var line in lines.Take(start - 1)) result.Append(line);
        for (var i = 0; i < replacement.Count; i++) result.Append(replacement[i]).Append(i == replacement.Count - 1 ? lastEnding : newline);
        // Deleting the file's last lines: the line before them now ends the file, so it keeps its ending as it was.
        foreach (var line in lines.Skip(end)) result.Append(line);
        return result.ToString();
    }

    /// <summary>
    /// "Apply suggestion from @alice", or for several "Apply suggestions from code review", crediting the people who
    /// suggested them as co-authors (not bots, and not <paramref name="committer"/>, who is the author anyway).
    /// </summary>
    public static string CommitMessage(IReadOnlyList<SuggestedChange> changes, string? committer = null)
    {
        var subject = changes.Count == 1
            ? $"Apply suggestion from @{changes[0].Author.Login} on {Path.GetFileName(changes[0].Path)}"
            : "Apply suggestions from code review";
        var coAuthors = changes.Select(c => c.Author)
            .Where(a => !a.IsBot && a.Login != "ghost" && !string.Equals(a.Login, committer, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(a => a.Login, StringComparer.OrdinalIgnoreCase)
            .Select(a => $"Co-authored-by: {a.DisplayName} <{(a.Id is { } id ? $"{id}+" : "")}{a.Login}@users.noreply.github.com>")
            .ToList();
        return coAuthors.Count == 0 ? subject : subject + "\n\n" + string.Join('\n', coAuthors);
    }

    /// <summary>
    /// Makes a commit on top of <paramref name="headSha"/> with the changes applied, without touching any worktree or
    /// the index (a temporary index builds the tree). Returns the new commit's id. Files must be UTF-8 text; a
    /// byte-order mark is kept.
    /// </summary>
    public static async Task<string> CommitAsync(string worktree, string headSha, IReadOnlyList<SuggestedChange> changes, string message)
    {
        if (changes.Count == 0) throw new ArgumentException("No suggestions to commit.", nameof(changes));
        var temp = Directory.CreateTempSubdirectory("totalgit-suggest");
        try
        {
            var index = Path.Combine(temp.FullName, "index");
            var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
            await GitCli.RunAsync(worktree, ["read-tree", headSha], env: env);

            foreach (var file in changes.GroupBy(c => c.Path))
            {
                // Bottom up, so earlier changes don't move the lines of later ones.
                var ordered = file.OrderByDescending(c => c.StartLine).ToList();
                for (var i = 1; i < ordered.Count; i++)
                    if (ordered[i].EndLine >= ordered[i - 1].StartLine)
                        throw new InvalidOperationException($"Two suggestions change the same lines of {file.Key}: commit them one at a time.");

                var entry = (await GitCli.RunAsync(worktree, "ls-tree", headSha, "--", file.Key)).StdOut.Trim();
                // "100644 blob <sha>\t<path>"
                var parts = entry.Split([' ', '\t'], 4);
                if (parts.Length < 4 || parts[1] != "blob") throw new InvalidOperationException($"{file.Key} isn't in the pull request's latest commit.");
                var original = Path.Combine(temp.FullName, "original");
                await GitCli.SaveBlobAsync(worktree, parts[2], original);
                var (text, bom) = ReadUtf8(await File.ReadAllBytesAsync(original), file.Key);

                foreach (var change in ordered) text = Apply(text, change.StartLine, change.EndLine, change.Replacement);

                var updated = Path.Combine(temp.FullName, "updated");
                var bytes = Encoding.UTF8.GetBytes(text);
                await File.WriteAllBytesAsync(updated, bom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes);
                // The bytes are the blob's own (already clean): no filters, or line endings could change.
                var blob = (await GitCli.RunAsync(worktree, "hash-object", "-w", "--no-filters", updated)).StdOut.Trim();
                await GitCli.RunAsync(worktree, ["update-index", "--cacheinfo", $"{parts[0]},{blob},{file.Key}"], env: env);
            }

            var tree = (await GitCli.RunAsync(worktree, ["write-tree"], env: env)).StdOut.Trim();
            return (await GitCli.RunAsync(worktree, ["commit-tree", tree, "-p", headSha, "-F", "-"], message + "\n")).StdOut.Trim();
        }
        finally
        {
            try { temp.Delete(recursive: true); } catch (IOException) { }
        }
    }

    private static (string Text, bool Bom) ReadUtf8(byte[] bytes, string path)
    {
        var bom = bytes is [0xEF, 0xBB, 0xBF, ..];
        try
        {
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException($"{path} isn't UTF-8 text, so suggestions can't be applied to it here. Use GitHub instead.");
        }
    }
}
