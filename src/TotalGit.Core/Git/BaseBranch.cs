namespace TotalGit.Core.Git;

/// <summary>Finds the branch another branch most likely came off, for comparing it the way a pull request would.</summary>
public static class BaseBranch
{
    /// <summary>
    /// The candidate closest below <paramref name="branch"/>: the one whose merge-base with it leaves the fewest
    /// commits on the branch. Candidates already containing the branch (merged into them, or the branch itself) are
    /// skipped; a tie goes to <paramref name="preferred"/> (the default branch), then to the first listed.
    /// </summary>
    public static async Task<string?> NearestAsync(string worktree, string branch, IEnumerable<string> candidates, string? preferred = null)
    {
        var tip = (await GitCli.RunAsync(worktree, ["rev-parse", "--verify", "--quiet", branch + "^{commit}"], throwOnError: false)).StdOut.Trim();
        if (tip.Length == 0) return null;
        string? best = null;
        var bestCount = int.MaxValue;
        foreach (var candidate in candidates.Distinct())
        {
            var mergeBase = await GitCli.RunAsync(worktree, ["merge-base", candidate, tip], throwOnError: false);
            var at = mergeBase.StdOut.Trim();
            if (mergeBase.ExitCode != 0 || at.Length == 0 || at == tip) continue;
            var count = await GitCli.RunAsync(worktree, ["rev-list", "--count", $"{at}..{tip}"], throwOnError: false);
            if (!int.TryParse(count.StdOut.Trim(), out var n)) continue;
            if (n < bestCount || (n == bestCount && candidate == preferred))
            {
                best = candidate;
                bestCount = n;
            }
        }
        return best;
    }
}
