namespace TotalGit.Core.Git;

/// <summary>Write operations, executed with the git CLI in a worktree folder.</summary>
public static class GitActions
{
    public static Task StageAsync(string worktree, IEnumerable<string> paths) =>
        GitCli.RunAsync(worktree, ["add", "-A", "--", .. paths]);

    public static Task StageAllAsync(string worktree) => GitCli.RunAsync(worktree, "add", "-A");

    public static async Task UnstageAsync(string worktree, IEnumerable<string> paths)
    {
        var list = paths.ToArray();
        if (await HasHeadAsync(worktree))
            await GitCli.RunAsync(worktree, ["restore", "--staged", "--", .. list]);
        else
            await GitCli.RunAsync(worktree, ["rm", "--cached", "-r", "-q", "--", .. list]);
    }

    public static async Task UnstageAllAsync(string worktree)
    {
        if (await HasHeadAsync(worktree))
            await GitCli.RunAsync(worktree, "reset", "-q");
        else
            await GitCli.RunAsync(worktree, "rm", "--cached", "-r", "-q", ".");
    }

    public static Task CommitAsync(string worktree, string message) =>
        GitCli.RunAsync(worktree, ["commit", "--cleanup=strip", "-F", "-"], stdin: message);

    /// <summary>Checks out a local branch. Throws <see cref="BranchInUseException"/> if another worktree has it.</summary>
    public static Task CheckoutAsync(string worktree, string branch) =>
        GitCli.RunAsync(worktree, "switch", branch);

    /// <summary>
    /// Checks out a remote branch: switches to the existing local branch of the same name, or
    /// creates one tracking the remote branch.
    /// </summary>
    public static async Task<string> CheckoutRemoteAsync(string worktree, string remoteName, string branch)
    {
        var exists = await GitCli.RunAsync(worktree, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], throwOnError: false);
        if (exists.ExitCode == 0)
            await GitCli.RunAsync(worktree, "switch", branch);
        else
            await GitCli.RunAsync(worktree, "switch", "--track", "-c", branch, $"{remoteName}/{branch}");
        return branch;
    }

    public static Task FetchAsync(string worktree) => GitCli.RunAsync(worktree, "fetch", "--all", "--prune");

    public static Task PullAsync(string worktree) => GitCli.RunAsync(worktree, "pull");

    /// <summary>Pushes the current branch, setting the upstream to <paramref name="remote"/> when it has none.</summary>
    public static async Task PushAsync(string worktree, string? remote = null)
    {
        var upstream = await GitCli.RunAsync(worktree, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], throwOnError: false);
        if (upstream.ExitCode == 0)
        {
            await GitCli.RunAsync(worktree, "push");
            return;
        }

        var branch = (await GitCli.RunAsync(worktree, "branch", "--show-current")).StdOut.Trim();
        if (branch.Length == 0) throw new InvalidOperationException("Cannot push a detached HEAD.");
        remote ??= await DefaultRemoteAsync(worktree)
            ?? throw new InvalidOperationException("This repository has no remotes to push to.");
        await GitCli.RunAsync(worktree, "push", "-u", remote, branch);
    }

    public static async Task<string?> DefaultRemoteAsync(string worktree)
    {
        var remotes = (await GitCli.RunAsync(worktree, "remote")).StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return remotes.Contains("origin") ? "origin" : remotes.FirstOrDefault();
    }

    private static async Task<bool> HasHeadAsync(string worktree) =>
        (await GitCli.RunAsync(worktree, ["rev-parse", "--verify", "--quiet", "HEAD"], throwOnError: false)).ExitCode == 0;
}
