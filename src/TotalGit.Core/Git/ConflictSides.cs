namespace TotalGit.Core.Git;

/// <summary>
/// What the two sides of a conflict are, in words: git labels them "HEAD" and a commit, which says little (in a
/// rebase, HEAD isn't your branch at all but the one you're rebasing onto).
/// </summary>
/// <param name="Ours">A short name for the B side, or null to keep git's label.</param>
/// <param name="OursRole">What the B side is, e.g. "the branch you're rebasing onto".</param>
/// <param name="Theirs">A short name for the C side, or null to keep git's label.</param>
/// <param name="TheirsRole">What the C side is.</param>
public sealed record ConflictSides(string? Ours, string OursRole, string? Theirs, string TheirsRole)
{
    /// <summary>The sides of the operation in progress, or null when there isn't one git describes.</summary>
    public static async Task<ConflictSides?> DescribeAsync(string worktree)
    {
        var gitDir = (await GitCli.RunAsync(worktree, "rev-parse", "--absolute-git-dir")).StdOut.Trim();
        string? Read(params string[] parts)
        {
            var file = Path.Combine([gitDir, .. parts]);
            try { return File.Exists(file) ? File.ReadAllText(file).Trim() : null; }
            catch (IOException) { return null; }
        }

        foreach (var dir in (string[])["rebase-merge", "rebase-apply"])
        {
            if (!Directory.Exists(Path.Combine(gitDir, dir))) continue;
            var branch = Read(dir, "head-name") is { } head ? ShortBranch(head) : null;
            var onto = Read(dir, "onto");
            var ontoName = onto is null ? null : await NameOfAsync(worktree, onto);
            // The step being replayed now is msgnum (rebase-merge) or next (rebase-apply); the ones before it are done.
            var step = int.TryParse(Read(dir, dir == "rebase-merge" ? "msgnum" : "next"), out var n) ? n : 1;
            var moved = Math.Max(0, step - 1);
            var oursRole = "what you're rebasing onto" + (moved > 0 ? $" + {moved} of your commits already moved" : "");
            var theirsRole = branch is null ? "your commit being moved" : $"your commit, moving from {branch}";
            return new ConflictSides(ontoName, oursRole, null, theirsRole);
        }

        var current = await CurrentBranchAsync(worktree);
        if (Read("MERGE_HEAD") is not null)
            return new ConflictSides(current, "your branch, being merged into", null, "the branch being merged in");
        if (Read("CHERRY_PICK_HEAD") is not null)
            return new ConflictSides(current, "your branch", null, "the commit being cherry-picked");
        if (Read("REVERT_HEAD") is not null)
            return new ConflictSides(current, "your branch", null, "undoing the commit being reverted");
        return null;
    }

    /// <summary>A branch at <paramref name="sha"/> (local before remote), or its short SHA.</summary>
    private static async Task<string> NameOfAsync(string worktree, string sha)
    {
        var names = (await GitCli.RunAsync(worktree, ["for-each-ref", "--points-at", sha, "--format=%(refname)", "refs/heads", "refs/remotes"],
            throwOnError: false)).StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var local = names.FirstOrDefault(r => r.StartsWith("refs/heads/", StringComparison.Ordinal));
        if (local is not null) return local["refs/heads/".Length..];
        var remote = names.FirstOrDefault(r => r.StartsWith("refs/remotes/", StringComparison.Ordinal) && !r.EndsWith("/HEAD", StringComparison.Ordinal));
        return remote is not null ? remote["refs/remotes/".Length..] : sha[..Math.Min(8, sha.Length)];
    }

    private static async Task<string?> CurrentBranchAsync(string worktree)
    {
        var result = await GitCli.RunAsync(worktree, ["symbolic-ref", "--quiet", "--short", "HEAD"], throwOnError: false);
        return result.ExitCode == 0 && result.StdOut.Trim() is { Length: > 0 } name ? name : null;
    }

    private static string ShortBranch(string refName) =>
        refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName["refs/heads/".Length..] : refName;
}
