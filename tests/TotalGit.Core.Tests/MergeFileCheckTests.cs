using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class MergeFileCheckTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    private string Full(string path) => Path.Combine(_repo.Root, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>main and topic both change <paramref name="path"/>, then main merges topic (and stops).</summary>
    private async Task Conflict(string path = "a.txt", string ours = "one\nTWO main\nthree\n", string theirs = "one\nTWO topic\nthree\n")
    {
        _repo.Commit("base", path, "one\ntwo\nthree\n");
        _repo.Git("switch", "-q", "-c", "topic");
        _repo.Commit("topic change", path, theirs);
        _repo.Git("switch", "-q", "main");
        _repo.Commit("main change", path, ours);
        Assert.NotEqual(OperationOutcome.Completed, await GitActions.MergeAsync(_repo.Root, "topic"));
    }

    [Theory]
    [InlineData("--merge|a.txt", "a.txt")]
    [InlineData("--merge|a b.txt|BASE|LOCAL|REMOTE", "a b.txt")]
    public void Reads_the_file_from_the_command_line(string args, string file) =>
        Assert.Equal(new MergeCommandLine(file), MergeCommandLine.Parse(args.Split('|')));

    [Theory]
    [InlineData("--merge")]
    [InlineData("--merge| ")]
    [InlineData("C:\\repo")]
    public void Refuses_a_command_line_without_a_file(string args) => Assert.Null(MergeCommandLine.Parse(args.Split('|')));

    [Fact]
    public async Task A_file_both_sides_changed_can_be_merged()
    {
        await Conflict("src/deep/a.txt");

        var check = await MergeFileCheck.RunAsync(Full("src/deep/a.txt"));

        Assert.NotNull(check.Target);
        Assert.Equal("src/deep/a.txt", check.Target!.Path);
        Assert.True(Directory.Exists(check.Target.GitDirectory));
        Assert.Equal(Path.GetFullPath(_repo.Root).TrimEnd('\\', '/'), check.Target.Worktree.TrimEnd('\\', '/'), ignoreCase: true);
        // Nothing staged by checking.
        Assert.Contains("UU src/deep/a.txt", _repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Taking_one_side_writes_it_and_leaves_the_index_to_the_caller()
    {
        await Conflict();

        await GitActions.CheckoutSideAsync(_repo.Root, "a.txt", ours: false);

        Assert.Equal("one\nTWO topic\nthree\n", File.ReadAllText(Full("a.txt")).Replace("\r\n", "\n"));
        Assert.Contains("UU a.txt", _repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Works_in_a_linked_worktree()
    {
        _repo.Commit("base", "a.txt", "one\ntwo\nthree\n");
        _repo.Git("branch", "topic");
        _repo.Commit("main change", "a.txt", "one\nTWO main\nthree\n");
        var linked = Path.Combine(_repo.Root, ".worktrees", "sync");
        _repo.Git("worktree", "add", "-q", linked, "topic");
        TestRepo.RunGit(linked, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "x");
        File.WriteAllText(Path.Combine(linked, "a.txt"), "one\nTWO topic\nthree\n");
        TestRepo.RunGit(linked, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-am", "topic change");
        Assert.NotEqual(OperationOutcome.Completed, await GitActions.MergeAsync(linked, "main"));

        var check = await MergeFileCheck.RunAsync(Path.Combine(linked, "a.txt"));

        Assert.NotNull(check.Target);
        Assert.Equal("a.txt", check.Target!.Path);
        Assert.Contains("worktrees", check.Target.GitDirectory);
    }

    [Fact]
    public async Task A_file_deleted_on_one_side_is_not_for_the_merge_window()
    {
        _repo.Commit("base", "a.txt", "one\n");
        _repo.Git("switch", "-q", "-c", "topic");
        _repo.Git("rm", "-q", "a.txt");
        _repo.Git("commit", "-q", "-m", "delete");
        _repo.Git("switch", "-q", "main");
        _repo.Commit("main change", "a.txt", "one changed\n");
        Assert.NotEqual(OperationOutcome.Completed, await GitActions.MergeAsync(_repo.Root, "topic"));

        var check = await MergeFileCheck.RunAsync(Full("a.txt"));

        Assert.Null(check.Target);
        Assert.Equal(MergeExit.Unsupported, check.Exit);
        Assert.Contains("deleted", check.Reason);
    }

    [Fact]
    public async Task A_file_that_isnt_conflicted_is_refused()
    {
        _repo.Commit("base", "a.txt", "one\n");

        var check = await MergeFileCheck.RunAsync(Full("a.txt"));

        Assert.Equal(MergeExit.Unsupported, check.Exit);
    }

    [Fact]
    public async Task A_conflict_already_fixed_by_hand_is_refused()
    {
        await Conflict();
        File.WriteAllText(Full("a.txt"), "one\nfixed\nthree\n");

        var check = await MergeFileCheck.RunAsync(Full("a.txt"));

        Assert.Equal(MergeExit.Unsupported, check.Exit);
        Assert.Contains("no conflict markers", check.Reason);
    }

    [Fact]
    public async Task Text_that_isnt_utf8_is_refused()
    {
        await Conflict();
        // "café" in Windows-1252 inside the conflict: 0xE9 alone isn't valid UTF-8.
        File.WriteAllBytes(Full("a.txt"), [.. "<<<<<<< HEAD\ncaf"u8, 0xE9, .. "\n=======\nx\n>>>>>>> topic\n"u8]);

        var check = await MergeFileCheck.RunAsync(Full("a.txt"));

        Assert.Equal(MergeExit.Unsupported, check.Exit);
        Assert.Contains("UTF-8", check.Reason);
    }

    [Fact]
    public async Task A_missing_file_or_a_folder_outside_git_is_a_usage_error()
    {
        _repo.Commit("base", "a.txt", "one\n");
        Assert.Equal(MergeExit.Usage, (await MergeFileCheck.RunAsync(Full("nope.txt"))).Exit);
        Assert.Equal(MergeExit.Usage, (await MergeFileCheck.RunAsync(Path.Combine(_repo.Root, "no-folder", "a.txt"))).Exit);

        var outside = Directory.CreateTempSubdirectory("totalgit-nogit");
        try
        {
            File.WriteAllText(Path.Combine(outside.FullName, "a.txt"), "x");
            var check = await MergeFileCheck.RunAsync(Path.Combine(outside.FullName, "a.txt"));
            Assert.Equal(MergeExit.Usage, check.Exit);
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }
}
