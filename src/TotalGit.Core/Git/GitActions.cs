namespace TotalGit.Core.Git;

public enum RebaseAction
{
    Pick,
    Reword,
    Squash,
    Fixup,
    Drop,
}

/// <param name="Message">New message for <see cref="RebaseAction.Reword"/>.</param>
public sealed record RebaseStep(RebaseAction Action, string Sha, string? Message = null);

/// <summary>Write operations, executed with the git CLI in a worktree folder.</summary>
public static class GitActions
{
    public static Task StageAsync(string worktree, IEnumerable<string> paths) => RunWithPathsAsync(worktree, ["add", "-A"], paths.ToArray());

    public static Task StageAllAsync(string worktree) => GitCli.RunAsync(worktree, "add", "-A");

    public static async Task UnstageAsync(string worktree, IEnumerable<string> paths)
    {
        var list = paths.ToArray();
        if (await HasHeadAsync(worktree))
            await RunWithPathsAsync(worktree, ["restore", "--staged"], list);
        else
            await RunWithPathsAsync(worktree, ["rm", "--cached", "-r", "-q"], list);
    }

    /// <summary>Runs a command on paths; long lists go through stdin so they can't exceed the command-line limit.</summary>
    private static Task RunWithPathsAsync(string worktree, string[] command, string[] paths) => paths.Length <= 50
        ? GitCli.RunAsync(worktree, [.. command, "--", .. paths])
        : GitCli.RunAsync(worktree, [.. command, "--pathspec-from-file=-", "--pathspec-file-nul"], stdin: string.Join('\0', paths));

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
    public static Task CheckoutAsync(string worktree, string branch, LocalChanges changes = LocalChanges.Keep) =>
        GitCli.RunAsync(worktree, ["switch", .. SwitchFlags(changes), branch]);

    /// <summary>
    /// What <c>git switch</c> does with uncommitted changes. <see cref="LocalChanges.Stash"/> is done by the caller
    /// beforehand (the tree is then clean), so it needs no flag.
    /// </summary>
    private static string[] SwitchFlags(LocalChanges changes) => changes switch
    {
        LocalChanges.Merge => ["--merge"],
        LocalChanges.Discard => ["--discard-changes"],
        _ => [],
    };

    /// <summary>
    /// Checks out a remote branch: switches to the existing local branch of the same name, or
    /// creates one tracking the remote branch.
    /// </summary>
    public static async Task<string> CheckoutRemoteAsync(string worktree, string remoteName, string branch, LocalChanges changes = LocalChanges.Keep)
    {
        var exists = await GitCli.RunAsync(worktree, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], throwOnError: false);
        if (exists.ExitCode == 0)
            await GitCli.RunAsync(worktree, ["switch", .. SwitchFlags(changes), branch]);
        else
            await GitCli.RunAsync(worktree, ["switch", .. SwitchFlags(changes), "--track", "-c", branch, $"{remoteName}/{branch}"]);
        return branch;
    }

    /// <summary>Deletes a local branch. Without <paramref name="force"/> git refuses unmerged branches.</summary>
    public static Task DeleteBranchAsync(string worktree, string branch, bool force = false) =>
        GitCli.RunAsync(worktree, "branch", force ? "-D" : "-d", branch);

    /// <summary>Creates a tag at <paramref name="sha"/>: annotated when a message is given, else lightweight.</summary>
    public static Task CreateTagAsync(string worktree, string name, string sha, string? message = null) =>
        string.IsNullOrWhiteSpace(message)
            ? GitCli.RunAsync(worktree, "tag", name, sha)
            : GitCli.RunAsync(worktree, ["tag", "-a", name, sha, "--cleanup=strip", "-F", "-"], stdin: message);

    public static Task DeleteTagAsync(string worktree, string name) => GitCli.RunAsync(worktree, "tag", "-d", name);

    /// <summary>Checks out a commit without a branch (detached HEAD). Uncommitted changes come along, as with a branch switch.</summary>
    public static Task CheckoutDetachedAsync(string worktree, string sha, LocalChanges changes = LocalChanges.Keep) =>
        GitCli.RunAsync(worktree, ["switch", .. SwitchFlags(changes), "--detach", sha]);

    /// <summary>Creates a branch at a commit, optionally switching to it.</summary>
    public static Task CreateBranchAsync(string worktree, string name, string sha, bool checkout) => checkout
        ? GitCli.RunAsync(worktree, "switch", "-c", name, sha)
        : GitCli.RunAsync(worktree, "branch", name, sha);

    public static Task PushTagAsync(string worktree, string remote, string name) =>
        GitCli.RunAsync(worktree, "push", remote, $"refs/tags/{name}");

    public static Task DeleteRemoteTagAsync(string worktree, string remote, string name) =>
        GitCli.RunAsync(worktree, "push", remote, $":refs/tags/{name}");

    /// <summary>The rules of <c>git check-ref-format</c> for a branch or tag name.</summary>
    public static bool IsValidRefName(string name)
    {
        if (name.Length == 0 || name == "@" || name.StartsWith('-') || name.StartsWith('/') || name.EndsWith('/')
            || name.EndsWith('.') || name.Contains("..") || name.Contains("@{") || name.Contains("//"))
            return false;
        if (name.Any(c => c < 32 || c == 127 || " ~^:?*[\\".Contains(c))) return false;
        return name.Split('/').All(part => !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));
    }

    /// <summary>Stashes all uncommitted changes (optionally including untracked files).</summary>
    public static Task StashAsync(string worktree, string? message, bool includeUntracked)
    {
        var args = new List<string> { "stash", "push" };
        if (includeUntracked) args.Add("--include-untracked");
        if (!string.IsNullOrWhiteSpace(message)) args.AddRange(["-m", message.Trim()]);
        return GitCli.RunAsync(worktree, args);
    }

    public static Task StashApplyAsync(string worktree, int index) => GitCli.RunAsync(worktree, "stash", "apply", $"stash@{{{index}}}");

    public static Task StashPopAsync(string worktree, int index) => GitCli.RunAsync(worktree, "stash", "pop", $"stash@{{{index}}}");

    public static Task StashDropAsync(string worktree, int index) => GitCli.RunAsync(worktree, "stash", "drop", $"stash@{{{index}}}");

    // Never let git open an editor: merge/squash messages are accepted as git proposes them.
    private static readonly Dictionary<string, string> NoEditor = new() { ["GIT_EDITOR"] = "true" };

    /// <summary>Merges <paramref name="revision"/> into the current branch. Stops (not throws) on conflicts.</summary>
    public static Task<OperationOutcome> MergeAsync(string worktree, string revision, bool noFastForward = false) =>
        RunStoppableAsync(worktree, ["merge", "--no-edit", noFastForward ? "--no-ff" : "--ff", revision]);

    /// <summary>Resolves a conflicted file with one side's whole version and stages it.</summary>
    public static async Task TakeSideAsync(string worktree, string path, bool ours)
    {
        await GitCli.RunAsync(worktree, "checkout", ours ? "--ours" : "--theirs", "--", path);
        await GitCli.RunAsync(worktree, "add", "--", path);
    }

    public static Task MergeAbortAsync(string worktree) => GitCli.RunAsync(worktree, "merge", "--abort");

    /// <summary>Concludes a merge whose conflicts are resolved and staged.</summary>
    public static Task MergeContinueAsync(string worktree) =>
        GitCli.RunAsync(worktree, ["commit", "--no-edit"], env: NoEditor);

    /// <summary>Rebases the current branch onto <paramref name="onto"/>.</summary>
    public static Task<OperationOutcome> RebaseAsync(string worktree, string onto) =>
        RunStoppableAsync(worktree, ["rebase", onto]);

    public static Task<OperationOutcome> RebaseContinueAsync(string worktree) =>
        RunStoppableAsync(worktree, ["rebase", "--continue"]);

    public static Task<OperationOutcome> RebaseSkipAsync(string worktree) =>
        RunStoppableAsync(worktree, ["rebase", "--skip"]);

    public static Task RebaseAbortAsync(string worktree) => GitCli.RunAsync(worktree, "rebase", "--abort");

    /// <summary>
    /// Rewrites the commits after <paramref name="baseSha"/> (all commits when null) following
    /// <paramref name="steps"/>, oldest first. Reword messages are applied with an amend straight after the pick.
    /// </summary>
    public static async Task<OperationOutcome> InteractiveRebaseAsync(string worktree, string? baseSha, IReadOnlyList<RebaseStep> steps)
    {
        var dir = Path.Combine(Path.GetTempPath(), "totalgit-rebase", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        var todo = new System.Text.StringBuilder();
        var n = 0;
        foreach (var step in steps)
        {
            switch (step.Action)
            {
                case RebaseAction.Drop:
                    todo.Append("drop ").Append(step.Sha).Append('\n');
                    break;
                case RebaseAction.Squash or RebaseAction.Fixup:
                    todo.Append(step.Action == RebaseAction.Squash ? "squash " : "fixup ").Append(step.Sha).Append('\n');
                    break;
                case RebaseAction.Reword when !string.IsNullOrWhiteSpace(step.Message):
                    var file = Path.Combine(dir, $"msg-{n++}.txt");
                    await File.WriteAllTextAsync(file, step.Message);
                    todo.Append("pick ").Append(step.Sha).Append('\n');
                    todo.Append("exec git commit --amend --only --no-verify --cleanup=strip -F \"").Append(ShPath(file)).Append("\"\n");
                    break;
                default:
                    todo.Append("pick ").Append(step.Sha).Append('\n');
                    break;
            }
        }
        var todoFile = Path.Combine(dir, "git-rebase-todo");
        await File.WriteAllTextAsync(todoFile, todo.ToString());

        // git runs the sequence editor through its shell with the todo path appended, so copying
        // our prepared list over it replaces the list git generated.
        var env = new Dictionary<string, string>(NoEditor) { ["GIT_SEQUENCE_EDITOR"] = $"cp \"{ShPath(todoFile)}\"" };
        return await RunStoppableAsync(worktree, ["rebase", "-i", baseSha ?? "--root"], env);
    }

    private static string ShPath(string path) => path.Replace('\\', '/');

    /// <summary>Runs a merge/rebase step: a non-zero exit that leaves the operation in progress means "stopped".</summary>
    private static async Task<OperationOutcome> RunStoppableAsync(string worktree, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null)
    {
        var argList = args.ToList();
        var result = await GitCli.RunAsync(worktree, argList, throwOnError: false, env: env ?? NoEditor);
        if (result.ExitCode == 0) return OperationOutcome.Completed;
        if (await IsOperationInProgressAsync(worktree)) return OperationOutcome.Stopped;
        throw new GitCommandException(string.Join(' ', argList), result);
    }

    public static async Task<bool> IsOperationInProgressAsync(string worktree)
    {
        foreach (var name in (string[])["MERGE_HEAD", "rebase-merge", "rebase-apply", "CHERRY_PICK_HEAD", "REVERT_HEAD"])
        {
            var path = (await GitCli.RunAsync(worktree, "rev-parse", "--git-path", name)).StdOut.Trim();
            if (!Path.IsPathRooted(path)) path = Path.Combine(worktree, path);
            if (File.Exists(path) || Directory.Exists(path)) return true;
        }
        return false;
    }

    public static async Task<bool> IsAncestorAsync(string worktree, string ancestor, string descendant) =>
        (await GitCli.RunAsync(worktree, ["merge-base", "--is-ancestor", ancestor, descendant], throwOnError: false)).ExitCode == 0;

    /// <summary>Commits after <paramref name="baseSha"/> that the current branch's upstream already has.</summary>
    public static async Task<IReadOnlySet<string>> PushedCommitsAsync(string worktree, string? baseSha)
    {
        var upstream = await GitCli.RunAsync(worktree, ["rev-parse", "--verify", "--quiet", "@{u}"], throwOnError: false);
        if (upstream.ExitCode != 0) return new HashSet<string>();
        var range = baseSha is null ? "@{u}" : $"{baseSha}..@{{u}}";
        var output = (await GitCli.RunAsync(worktree, "rev-list", range)).StdOut;
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
    }

    /// <summary>Commits in <c>base..HEAD</c> following first parents, oldest first (for an interactive rebase).</summary>
    public static async Task<IReadOnlyList<(string Sha, string Subject, bool IsMerge)>> CommitsSinceAsync(string worktree, string? baseSha)
    {
        var range = baseSha is null ? "HEAD" : $"{baseSha}..HEAD";
        var output = (await GitCli.RunAsync(worktree, "log", "--reverse", "--first-parent", "--format=%H%x09%P%x09%s", range)).StdOut;
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 3))
            .Select(p => (p[0], p.Length > 2 ? p[2] : "", p[1].Contains(' ')))
            .ToList();
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

    // ------------------------------------------------------------------ ignore rules

    /// <summary>
    /// What adding <paramref name="rules"/> (.gitignore lines) would do: the untracked files they would hide,
    /// and how many tracked files match (ignore rules don't affect those).
    /// </summary>
    public static async Task<IgnorePreview> PreviewIgnoreAsync(string worktree, string rules, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rules)) return new IgnorePreview([], 0);
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, rules.ReplaceLineEndings("\n") + "\n", ct);
            var untracked = GitCli.RunAsync(worktree, ["ls-files", "-z", "--others", "--ignored", $"--exclude-from={file}"], throwOnError: false, ct: ct);
            var tracked = GitCli.RunAsync(worktree, ["ls-files", "-z", "--cached", "--ignored", $"--exclude-from={file}"], throwOnError: false, ct: ct);
            return new IgnorePreview(SplitNul((await untracked).StdOut), SplitNul((await tracked).StdOut).Count);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Appends rules to the worktree's .gitignore or the repository's info/exclude, skipping ones already there.</summary>
    public static async Task AddIgnoreRulesAsync(string worktree, string rules, IgnoreTarget target)
    {
        var path = target == IgnoreTarget.GitIgnore
            ? Path.Combine(worktree, ".gitignore")
            : Path.GetFullPath((await GitCli.RunAsync(worktree, "rev-parse", "--git-path", "info/exclude")).StdOut.Trim(), worktree);

        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
        var present = Lines(existing).ToHashSet();
        var add = Lines(rules).Where(l => l.Length > 0 && present.Add(l)).ToList();
        if (add.Count == 0) return;

        var newline = existing.Contains("\r\n") ? "\r\n" : "\n";
        var text = (existing.Length > 0 && !existing.EndsWith('\n') ? newline : "") + string.Join(newline, add) + newline;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, text);
    }

    private static IEnumerable<string> Lines(string text) => text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim());

    private static List<string> SplitNul(string text) => text.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
}
