using TotalGit.Core.Avatars;
using TotalGit.Core.Git;
using TotalGit.Core.Hosting;

namespace TotalGit.App.ViewModels;

/// <summary>Who remote branches belong to (an avatar on their sidebar rows), and Jira keys in branch names.</summary>
public partial class RepositoryViewModel
{
    private int _ownersRequest;
    private string? _ownersSignature;

    // Owners of branches beyond the loaded history, asked of git once per tip (and set of main lines).
    private readonly Dictionary<string, BranchOwner?> _ownerCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Works out remote branches' owners from the loaded history (git for branches it doesn't reach far enough
    /// for) and shows them in the sidebar. Does nothing when neither the history nor the branches changed.
    /// </summary>
    private async void UpdateBranchOwners(RepositoryState state)
    {
        // The default branch is everyone's: it gets no owner.
        var remotes = state.Refs.Where(r => r.Kind == RefKind.RemoteBranch && r.ShortName != "HEAD" && r.ShortName != state.DefaultBranch).ToList();
        var mainLines = state.Refs.Where(r => r.Kind is RefKind.LocalBranch or RefKind.RemoteBranch && BranchCategory.IsMainLine(r.ShortName)).ToList();
        var mainTips = mainLines.Select(r => r.TargetSha).Distinct().ToList();
        var signature = $"{_commits.Count}|{(_commits.Count > 0 ? _commits[0].Sha : "")}|"
            + string.Join(",", remotes.Select(r => r.Name + "=" + r.TargetSha)) + "|" + string.Join(",", mainLines.Select(r => r.Name));
        if (signature == _ownersSignature) return;
        _ownersSignature = signature;

        var request = ++_ownersRequest;
        var commits = _commits.ToList();
        var worktree = state.WorkingDirectory;
        var github = AvatarIdentity.ParseGitHubRemote(state.OriginUrl);
        try
        {
            var owners = await Task.Run(async () =>
            {
                var (found, unresolved) = BranchOwnership.Compute(commits, remotes, mainLines,
                    r => BranchCategory.IsMainLine(r.ShortName));
                // Beyond the loaded history: git, a few at a time.
                using var gate = new SemaphoreSlim(4);
                var extra = await Task.WhenAll(unresolved.Select(async r =>
                {
                    var isMain = BranchCategory.IsMainLine(r.ShortName);
                    var exclude = isMain ? BranchOwnership.OtherMainLineTips(mainLines, r) : mainTips;
                    var key = r.TargetSha + "|" + string.Join(",", exclude);
                    lock (_ownerCache)
                        if (_ownerCache.TryGetValue(key, out var cached)) return (r.Name, cached);
                    await gate.WaitAsync();
                    try
                    {
                        var own = await GitActions.OwnCommitsAsync(worktree, r.TargetSha, exclude);
                        // A main line with nothing of its own is shared: no owner rather than a guess.
                        var tip = own.Count > 0 || isMain ? null : (await GitActions.OwnCommitsAsync(worktree, r.TargetSha, [], max: 1)).FirstOrDefault();
                        var owner = BranchOwnership.FromOwnCommits(own, tip);
                        lock (_ownerCache) _ownerCache[key] = owner;
                        return (r.Name, owner);
                    }
                    finally { gate.Release(); }
                }));
                foreach (var (name, owner) in extra)
                    if (owner is not null) found[name] = owner;
                return found;
            });
            if (request != _ownersRequest || _state is null) return;
            Sidebar.SetOwners(owners, github);
        }
        catch (Exception ex) when (ex is GitCommandException or IOException or InvalidOperationException)
        {
            // Owners are a nicety: leave the rows without them.
        }
    }

    /// <summary>Jira projects remembered for this repository, so lower-case keys in branch names are recognised.</summary>
    private void LoadJiraProjects(RepositoryState state) =>
        Sidebar.JiraProjects = (_settings.FindRepository(state.MainWorkingDirectory)?.JiraProjects ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers the Jira projects in pull request titles ("E4") for recognising keys in branch names.</summary>
    private void LearnJiraProjects(IReadOnlyList<PullRequestSummary> pullRequests)
    {
        if (_state is null) return;
        var seen = pullRequests.Select(p => PullRequestTriage.Ticket(p.Title).Key).OfType<string>()
            .Select(PullRequestTriage.Project).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var repo = _settings.ForRepository(_state.MainWorkingDirectory);
        var known = repo.JiraProjects ?? [];
        var added = seen.Where(p => !known.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        if (added.Count == 0) return;
        repo.JiraProjects = [.. known, .. added];
        _settings.Save();
        Sidebar.JiraProjects = repo.JiraProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The ticket key in a branch's name ("E4-2361"), if it has one.</summary>
    private string? TicketInBranch(string name) =>
        PullRequestTriage.KeyInBranch(name, Sidebar.JiraProjects)?.Key;
}
