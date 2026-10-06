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

public enum PushRejection
{
    /// <summary>The branch's history was rewritten (rebase, amend): the remote has commits it no longer contains.</summary>
    NonFastForward,

    /// <summary>The remote has commits that were never fetched: someone else pushed.</summary>
    FetchFirst,

    /// <summary>A force push was refused because the remote branch moved since the last fetch.</summary>
    StaleLease,

    /// <summary>
    /// A force push was refused because the remote branch has fetched commits that were never on the local branch
    /// (someone else pushed, or it was changed on the server), so replacing them may lose work.
    /// </summary>
    UnseenRemoteCommits,
}

/// <param name="UnpushedCount">Commits on the branch that no remote branch contains.</param>
public sealed record BranchSummary(string Name, DateTimeOffset LastCommit, string Subject, int UnpushedCount);

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

    /// <summary>
    /// Throws away unstaged changes: tracked files go back to their staged version (what's staged is kept) and
    /// untracked files are deleted. Conflicted files are left alone.
    /// </summary>
    public static async Task DiscardUnstagedAsync(string worktree, IReadOnlyList<FileChange> files)
    {
        var tracked = files.Where(f => f.Kind is not (ChangeKind.Untracked or ChangeKind.Conflicted)).Select(f => f.Path).ToArray();
        var untracked = files.Where(f => f.Kind == ChangeKind.Untracked).Select(f => f.Path).ToArray();
        if (tracked.Length > 0) await RunWithPathsAsync(worktree, ["restore", "--worktree"], tracked);
        // clean has no --pathspec-from-file, so long lists go in batches.
        foreach (var batch in untracked.Chunk(50)) await GitCli.RunAsync(worktree, ["clean", "-f", "-q", "--", .. batch]);
    }

    /// <summary>
    /// Throws away every change to staged files, in the index and the working copy: they go back to HEAD (a file
    /// HEAD doesn't have is removed, and a rename brings the old name back).
    /// </summary>
    public static async Task DiscardStagedAsync(string worktree, IReadOnlyList<FileChange> files)
    {
        var paths = files.Where(f => f.Kind != ChangeKind.Conflicted)
            .SelectMany(f => f.OldPath is { } old && old != f.Path ? new[] { f.Path, old } : [f.Path])
            .Distinct().ToArray();
        if (paths.Length == 0) return;
        if (await HasHeadAsync(worktree))
            await RunWithPathsAsync(worktree, ["restore", "--source=HEAD", "--staged", "--worktree"], paths);
        else
            await RunWithPathsAsync(worktree, ["rm", "-r", "-f", "-q"], paths);
    }

    /// <summary>Throws away all uncommitted changes to tracked files and, if asked, deletes untracked files too.</summary>
    public static async Task DiscardAllAsync(string worktree, bool includeUntracked)
    {
        if (await HasHeadAsync(worktree))
            await GitCli.RunAsync(worktree, "reset", "--hard", "-q");
        else
            await GitCli.RunAsync(worktree, "rm", "-r", "-f", "-q", "--cached", "--ignore-unmatch", ".");
        if (includeUntracked) await GitCli.RunAsync(worktree, "clean", "-f", "-d", "-q");
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

    /// <summary>
    /// For branches about to be cleaned up: each tip's date and subject, and how many of its commits are on no
    /// remote branch. That count is only a hint: after a squash merge the work is on main under other commits.
    /// </summary>
    public static async Task<IReadOnlyList<BranchSummary>> GetBranchSummariesAsync(string worktree, IEnumerable<string> branches)
    {
        var wanted = branches.ToHashSet(StringComparer.Ordinal);
        var refs = await GitCli.RunAsync(worktree, "for-each-ref", "--format=%(refname:short)%09%(committerdate:unix)%09%(contents:subject)", "refs/heads");
        var result = new List<BranchSummary>();
        foreach (var line in refs.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t', 3);
            if (parts.Length < 2 || !wanted.Contains(parts[0])) continue;
            var when = long.TryParse(parts[1], out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.MinValue;
            var count = await GitCli.RunAsync(worktree, "rev-list", "--count", $"refs/heads/{parts[0]}", "--not", "--remotes");
            result.Add(new BranchSummary(parts[0], when, parts.Length > 2 ? parts[2] : "", int.TryParse(count.StdOut.Trim(), out var n) ? n : 0));
        }
        return result;
    }

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

    /// <summary>Deletes a branch on the remote (its remote-tracking branch goes too). Local branches are kept.</summary>
    public static Task DeleteRemoteBranchAsync(string worktree, string remote, string branch) =>
        GitCli.RunAsync(worktree, "push", remote, "--delete", $"refs/heads/{branch}");

    /// <summary>
    /// The authors of a branch's own commits, newest first: those reachable from <paramref name="tip"/> but not from
    /// <paramref name="exclude"/> (the main lines), merges left out. For branches beyond the loaded history.
    /// </summary>
    public static async Task<IReadOnlyList<OwnCommit>> OwnCommitsAsync(string worktree, string tip, IEnumerable<string> exclude, int max = 200)
    {
        var result = await GitCli.RunAsync(worktree,
            ["log", "--no-merges", "--first-parent", $"-n{max}", "--format=%H%x00%an%x00%ae%x00%ct", tip, "--not", .. exclude, "--"],
            throwOnError: false);
        if (result.ExitCode != 0) return [];
        var list = new List<OwnCommit>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.TrimEnd('\r').Split('\0');
            if (p.Length < 4) continue;
            list.Add(new OwnCommit(p[0], p[1], p[2], long.TryParse(p[3], out var t) ? DateTimeOffset.FromUnixTimeSeconds(t) : default));
        }
        return list;
    }

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

    /// <summary>
    /// The conflicted file with the common ancestor's text in each conflict (diff3-style markers), rebuilt from
    /// the index's base/ours/theirs versions. Null when there is no ancestor (added on both sides), when the file
    /// already has base sections, or when it no longer matches a fresh merge (e.g. it was edited by hand).
    /// </summary>
    /// <param name="current">The file's text as it is now (without a BOM).</param>
    public static async Task<string?> ConflictWithBaseAsync(string worktree, string path, string current, string oursLabel, string theirsLabel)
    {
        // Writes each stage to a temporary file in the worktree: "base ours theirs<TAB>path", "." for a missing one.
        var listed = await GitCli.RunAsync(worktree, ["checkout-index", "--stage=all", "--temp", "--", path], throwOnError: false);
        var names = listed.ExitCode == 0 ? listed.StdOut.Split('\t')[0].Trim().Split(' ') : [];
        var temps = names.Where(n => n != "." && n.Length > 0).Select(n => Path.Combine(worktree, n)).ToList();
        try
        {
            if (names.Length != 3 || names.Contains(".")) return null;
            string[] files = [Path.Combine(worktree, names[1]), Path.Combine(worktree, names[0]), Path.Combine(worktree, names[2])];
            string[] labels = ["-L", oursLabel, "-L", "base", "-L", theirsLabel];

            // merge-file exits with the number of conflicts, so a non-zero exit is expected.
            var plain = await GitCli.RunAsync(worktree, ["merge-file", "-p", .. labels, .. files], throwOnError: false);
            if (plain.ExitCode < 0 || Normalize(plain.StdOut) != Normalize(current)) return null;
            var withBase = await GitCli.RunAsync(worktree, ["merge-file", "-p", "--diff3", .. labels, .. files], throwOnError: false);
            if (withBase.ExitCode <= 0) return null;
            var text = withBase.StdOut.TrimStart('﻿').Replace("\r\n", "\n");
            return current.Contains("\r\n") ? text.Replace("\n", "\r\n") : text;
        }
        finally
        {
            foreach (var t in temps)
                try { File.Delete(t); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        static string Normalize(string s) => s.TrimStart('﻿').Replace("\r\n", "\n");
    }

    public static Task MergeAbortAsync(string worktree) => GitCli.RunAsync(worktree, "merge", "--abort");

    /// <summary>Concludes a merge whose conflicts are resolved and staged.</summary>
    public static Task MergeContinueAsync(string worktree) =>
        GitCli.RunAsync(worktree, ["commit", "--no-edit"], env: NoEditor);

    /// <summary>Rebases the current branch onto <paramref name="onto"/>.</summary>
    public static Task<OperationOutcome> RebaseAsync(string worktree, string onto) =>
        RunStoppableAsync(worktree, ["rebase", onto]);

    /// <summary>
    /// Replays the commits of <paramref name="branch"/> after <paramref name="upstream"/> (all of them when null) onto
    /// <paramref name="onto"/>, then points <paramref name="branch"/> at the result. Checks the branch out.
    /// </summary>
    public static Task<OperationOutcome> RebaseOntoAsync(string worktree, string onto, string? upstream, string branch) =>
        RunStoppableAsync(worktree, ["rebase", "--onto", onto, upstream ?? "--root", branch]);

    /// <summary>"abc1234 subject" for each commit in a revision range (e.g. <c>main..feature</c>), newest first.</summary>
    public static async Task<IReadOnlyList<string>> CommitSummariesAsync(string worktree, string range)
    {
        var result = await GitCli.RunAsync(worktree, ["log", "--format=%h %s", range], throwOnError: false);
        return result.ExitCode != 0 ? [] : result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>The commits in a revision range, newest first: full SHA, "abc1234 subject" and author.</summary>
    public static async Task<IReadOnlyList<(string Sha, string Summary, string Author)>> CommitListAsync(string worktree, string range)
    {
        var result = await GitCli.RunAsync(worktree, ["log", "--format=%H%x00%h %s%x00%an", range, "--"], throwOnError: false);
        if (result.ExitCode != 0) return [];
        return result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split('\0'))
            .Where(p => p.Length >= 3)
            .Select(p => (p[0], p[1], p[2]))
            .ToList();
    }

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

    /// <summary>
    /// Why <paramref name="oldestFirst"/> can't be squashed on the checked-out branch, or null when it can: the commits
    /// must be one unbroken first-parent line in HEAD's history, with no merge commits after them (a rebase would
    /// flatten those).
    /// </summary>
    public static async Task<string?> SquashProblemAsync(string worktree, IReadOnlyList<string> oldestFirst)
    {
        if (oldestFirst.Count < 2) return "Select at least two commits to squash.";
        if (!await IsAncestorAsync(worktree, oldestFirst[^1], "HEAD"))
            return "These commits aren't on the checked-out branch. Check out their branch to squash them.";
        var commits = await CommitsSinceAsync(worktree, await ParentOfAsync(worktree, oldestFirst[0]));
        if (commits.Count < oldestFirst.Count || !commits.Take(oldestFirst.Count).Select(c => c.Sha).SequenceEqual(oldestFirst))
            return "These commits aren't one unbroken line on the checked-out branch.";
        if (commits.Skip(oldestFirst.Count).Any(c => c.IsMerge))
            return "There are merge commits after these on the branch, and squashing would flatten them.";
        return null;
    }

    /// <summary>
    /// Squashes <paramref name="oldestFirst"/> (an unbroken line on the checked-out branch, see
    /// <see cref="SquashProblemAsync"/>) into one commit with <paramref name="message"/>, replaying the commits after
    /// them on top.
    /// </summary>
    public static async Task<OperationOutcome> SquashAsync(string worktree, IReadOnlyList<string> oldestFirst, string message)
    {
        if (await SquashProblemAsync(worktree, oldestFirst) is { } problem) throw new InvalidOperationException(problem);
        var baseSha = await ParentOfAsync(worktree, oldestFirst[0]);
        var after = (await CommitsSinceAsync(worktree, baseSha)).Skip(oldestFirst.Count);
        // The oldest takes the new message; the others fold into it keeping that message; the rest replay as they were.
        List<RebaseStep> steps = [new(RebaseAction.Reword, oldestFirst[0], message)];
        steps.AddRange(oldestFirst.Skip(1).Select(sha => new RebaseStep(RebaseAction.Fixup, sha)));
        steps.AddRange(after.Select(c => new RebaseStep(RebaseAction.Pick, c.Sha)));
        return await InteractiveRebaseAsync(worktree, baseSha, steps);
    }

    /// <summary>A commit's first parent, or null for a root commit.</summary>
    private static async Task<string?> ParentOfAsync(string worktree, string sha)
    {
        var result = await GitCli.RunAsync(worktree, ["rev-parse", "--verify", "--quiet", sha + "^"], throwOnError: false);
        return result.ExitCode == 0 ? result.StdOut.Trim() : null;
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

    /// <summary>Moves the current branch (or detached HEAD) to <paramref name="sha"/>.</summary>
    public static Task ResetAsync(string worktree, string sha, ResetMode mode) =>
        GitCli.RunAsync(worktree, "reset", "--" + mode.ToString().ToLowerInvariant(), sha);

    /// <summary>
    /// What resetting to <paramref name="sha"/> takes off the branch: commits after it on the current branch, and how
    /// many of those the upstream already has (removing them means a force push).
    /// </summary>
    public static async Task<(int Removed, int RemovedPushed)> ResetImpactAsync(string worktree, string sha)
    {
        var removed = await CountAsync(worktree, $"{sha}..HEAD");
        var upstream = await GitCli.RunAsync(worktree, ["rev-parse", "--verify", "--quiet", "@{u}"], throwOnError: false);
        if (upstream.ExitCode != 0 || removed == 0) return (removed, 0);
        var notPushed = await CountAsync(worktree, $"{sha}..HEAD", "^@{u}");
        return (removed, removed - notPushed);
    }

    private static async Task<int> CountAsync(string worktree, params string[] revisions) =>
        int.Parse((await GitCli.RunAsync(worktree, ["rev-list", "--count", .. revisions])).StdOut.Trim(), System.Globalization.CultureInfo.InvariantCulture);

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

    /// <summary>
    /// Fetches specific refs from <paramref name="remote"/>, e.g. <c>+refs/pull/12/head:refs/totalgit/pr/12</c> for a
    /// pull request's head, which no branch refspec covers.
    /// </summary>
    public static Task FetchRefsAsync(string worktree, string remote, params string[] refspecs) =>
        GitCli.RunAsync(worktree, ["fetch", "--no-tags", remote, .. refspecs]);

    /// <summary>Points a ref at a commit (creating it), e.g. to keep a reviewed pull request head from being collected.</summary>
    public static Task UpdateRefAsync(string worktree, string refName, string sha) =>
        GitCli.RunAsync(worktree, "update-ref", refName, sha);

    /// <summary>Whether a commit is in the local object database.</summary>
    public static async Task<bool> HasCommitAsync(string worktree, string sha) =>
        (await GitCli.RunAsync(worktree, ["cat-file", "-e", sha + "^{commit}"], throwOnError: false)).ExitCode == 0;

    /// <summary>The paths, of those given, that .gitattributes mark <c>linguist-generated</c>.</summary>
    public static async Task<IReadOnlySet<string>> GeneratedAttrAsync(string worktree, IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (list.Count == 0) return result;
        var output = await GitCli.RunAsync(worktree, ["check-attr", "linguist-generated", "--stdin"], string.Join('\n', list) + "\n", throwOnError: false);
        if (output.ExitCode != 0) return result;
        // Lines look like "path: linguist-generated: true".
        foreach (var line in output.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line.LastIndexOf(": ", StringComparison.Ordinal);
            var attr = value > 0 ? line.LastIndexOf(": ", value - 1, StringComparison.Ordinal) : -1;
            if (attr > 0 && line[(value + 2)..].Trim() is "true" or "set") result.Add(line[..attr]);
        }
        return result;
    }

    public static Task PullAsync(string worktree) => GitCli.RunAsync(worktree, "pull");

    /// <summary>Pulls only if the branch can simply move forward (no merge or rebase).</summary>
    public static Task PullFastForwardAsync(string worktree) => GitCli.RunAsync(worktree, "pull", "--ff-only");

    /// <summary>
    /// Moves a local branch that isn't checked out forward to its remote branch, fetching first. git refuses when the
    /// branch has commits of its own (it can't just move forward) or is checked out in a worktree.
    /// </summary>
    public static Task FastForwardBranchAsync(string worktree, string remote, string remoteBranch, string localBranch) =>
        GitCli.RunAsync(worktree, "fetch", "--no-tags", remote, $"refs/heads/{remoteBranch}:refs/heads/{localBranch}");

    /// <summary>
    /// Pushes the current branch, setting the upstream to <paramref name="remote"/> when it has none.
    /// <paramref name="force"/> replaces the remote branch (after a rebase or amend), but only if it is still where
    /// our last fetch saw it, so commits someone else pushed meanwhile are never silently dropped.
    /// </summary>
    /// <param name="replaceUnseen">
    /// With <paramref name="force"/>: also replace remote commits that were fetched but never on the local branch
    /// (drops <c>--force-if-includes</c>; the lease still refuses commits that were never fetched).
    /// </param>
    public static async Task PushAsync(string worktree, string? remote = null, bool force = false, bool replaceUnseen = false)
    {
        string[] forceFlags = !force ? [] : replaceUnseen ? ["--force-with-lease"] : ["--force-with-lease", "--force-if-includes"];
        var upstream = await GitCli.RunAsync(worktree, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], throwOnError: false);
        if (upstream.ExitCode == 0)
        {
            await GitCli.RunAsync(worktree, ["push", .. forceFlags]);
            return;
        }

        var branch = (await GitCli.RunAsync(worktree, "branch", "--show-current")).StdOut.Trim();
        if (branch.Length == 0) throw new InvalidOperationException("Cannot push a detached HEAD.");
        remote ??= await DefaultRemoteAsync(worktree)
            ?? throw new InvalidOperationException("This repository has no remotes to push to.");
        await GitCli.RunAsync(worktree, ["push", .. forceFlags, "-u", remote, branch]);
    }

    /// <summary>Commits on the current branch's upstream that HEAD doesn't have: what a force push drops from the remote.</summary>
    public static async Task<IReadOnlyList<string>> CommitsOnlyOnUpstreamAsync(string worktree)
    {
        var result = await GitCli.RunAsync(worktree, ["log", "--format=%h %s", "HEAD..@{u}"], throwOnError: false);
        return result.ExitCode == 0
            ? result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }

    /// <summary>Recognises why git refused a push, or null for other errors.</summary>
    public static PushRejection? ClassifyPushError(string message)
    {
        if (message.Contains("stale info", StringComparison.Ordinal)) return PushRejection.StaleLease;
        if (!message.Contains("[rejected]", StringComparison.Ordinal)) return null;
        if (message.Contains("remote ref updated since checkout", StringComparison.Ordinal)) return PushRejection.UnseenRemoteCommits;
        if (message.Contains("fetch first", StringComparison.Ordinal)) return PushRejection.FetchFirst;
        if (message.Contains("non-fast-forward", StringComparison.Ordinal)) return PushRejection.NonFastForward;
        return null;
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
