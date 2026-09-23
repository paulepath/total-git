using LibGit2Sharp;

namespace TotalGit.Core.Git;

public sealed class RepositoryOpenException(string message, Exception? inner = null) : Exception(message, inner);

public static class RepositoryReader
{
    public const int DefaultMaxCommits = 2000;

    static RepositoryReader()
    {
        // libgit2 refuses repos declaring config extensions it doesn't know. These only change how
        // worktree metadata is stored, which a read-only history viewer never touches.
        var known = GlobalSettings.GetExtensions();
        GlobalSettings.SetExtensions([.. known, "relativeworktrees"]);
    }

    /// <summary>Opens the repository containing <paramref name="path"/> and reads its refs and commit history.</summary>
    public static RepositorySnapshot Read(string path, int maxCommits = DefaultMaxCommits)
    {
        var gitDir = Repository.Discover(path)
            ?? throw new RepositoryOpenException($"'{path}' is not inside a git repository.");

        Repository repo;
        try
        {
            repo = new Repository(gitDir);
        }
        catch (LibGit2SharpException ex)
        {
            throw new RepositoryOpenException($"Unable to open repository: {ex.Message}", ex);
        }

        using (repo)
        {
            var refs = ReadRefs(repo);
            var commits = ReadCommits(repo, maxCommits);

            var root = repo.Info.WorkingDirectory ?? repo.Info.Path;
            var name = new DirectoryInfo(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
            var current = repo.Info.IsHeadDetached || repo.Info.IsHeadUnborn ? null : repo.Head.FriendlyName;
            var origin = repo.Network.Remotes["origin"]?.Url;

            return new RepositorySnapshot(root, name, current, origin, commits, refs);
        }
    }

    private static List<RefInfo> ReadRefs(Repository repo)
    {
        var result = new List<RefInfo>();
        var headSha = repo.Head.Tip?.Sha;

        foreach (var branch in repo.Branches)
        {
            if (branch.Tip is null) continue;
            // Skip symbolic refs like origin/HEAD.
            if (branch.IsRemote && branch.CanonicalName.EndsWith("/HEAD", StringComparison.Ordinal)) continue;

            result.Add(new RefInfo(
                branch.FriendlyName,
                branch.IsRemote ? RefKind.RemoteBranch : RefKind.LocalBranch,
                branch.Tip.Sha,
                branch.IsCurrentRepositoryHead));
        }

        foreach (var tag in repo.Tags)
        {
            var target = tag.PeeledTarget as Commit ?? tag.Target as Commit;
            if (target is null) continue;
            result.Add(new RefInfo(tag.FriendlyName, RefKind.Tag, target.Sha, false));
        }

        if (repo.Info.IsHeadDetached && headSha is not null)
            result.Add(new RefInfo("HEAD", RefKind.DetachedHead, headSha, true));

        return result;
    }

    private static List<CommitInfo> ReadCommits(Repository repo, int maxCommits)
    {
        var tips = repo.Refs
            .Where(r => !r.CanonicalName.StartsWith("refs/stash", StringComparison.Ordinal))
            .Select(r => r.ResolveToDirectReference()?.Target)
            .OfType<Commit>()
            .Cast<object>()
            .ToList();

        if (repo.Head.Tip is { } head) tips.Add(head);
        if (tips.Count == 0) return [];

        var filter = new CommitFilter
        {
            IncludeReachableFrom = tips,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
        };

        var result = new List<CommitInfo>();
        foreach (var c in repo.Commits.QueryBy(filter))
        {
            result.Add(new CommitInfo(
                c.Sha,
                c.Parents.Select(p => p.Sha).ToArray(),
                c.Author.Name,
                c.Author.Email,
                c.Author.When,
                c.MessageShort));
            if (result.Count >= maxCommits) break;
        }
        return result;
    }
}
