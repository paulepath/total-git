using TotalGit.Core.Hosting;

namespace TotalGit.Core.Tests;

public sealed class SuggestionsTests
{
    private static readonly PrUser Alice = new("alice", null, "Alice Smith") { Id = 42 };

    [Fact]
    public void Finds_suggestion_blocks_and_ignores_other_code()
    {
        const string body = "Rename it:\r\n```suggestion\r\nvar total = 0;\r\n    total++;\r\n```\r\n```cs\nnot this\n```\n````suggestion\n```\n````\nAnd delete:\n```suggestion\n```\n";

        var blocks = Suggestions.Parse(body);

        Assert.Equal(3, blocks.Count);
        Assert.Equal(["var total = 0;", "    total++;"], blocks[0]);
        Assert.Equal(["```"], blocks[1]); // a longer fence can hold a shorter one
        Assert.Empty(blocks[2]); // deletes the lines
    }

    [Fact]
    public void Replaced_lines_come_from_the_end_of_the_hunks_new_side()
    {
        const string hunk = "@@ -1,4 +1,4 @@\n a\n-b\n+B\n c\n d";
        Assert.Equal(["c", "d"], Suggestions.ReplacedLines(hunk, 2));
        Assert.Equal(["a", "B", "c", "d"], Suggestions.ReplacedLines(hunk, 4));
        Assert.Null(Suggestions.ReplacedLines(hunk, 5));
    }

    [Theory]
    [InlineData("a\r\nb\r\nc\r\n", 2, 2, "a\r\nX\r\nY\r\nc\r\n")]
    [InlineData("a\nb\nc", 3, 3, "a\nb\nX\nY")] // the last line had no ending, and still hasn't
    [InlineData("a\nb\nc\n", 1, 3, "X\nY\n")]
    public void Applies_keeping_line_endings(string text, int start, int end, string expected) =>
        Assert.Equal(expected, Suggestions.Apply(text, start, end, ["X", "Y"]));

    [Fact]
    public void Empty_suggestion_deletes_the_lines()
    {
        Assert.Equal("a\nc\n", Suggestions.Apply("a\nb\nc\n", 2, 2, []));
        Assert.Throws<InvalidOperationException>(() => Suggestions.Apply("a\n", 2, 2, ["x"]));
    }

    [Fact]
    public void Message_names_the_suggester_and_credits_people_not_bots()
    {
        var one = new SuggestedChange("src/app.cs", 1, 1, ["x"], Alice);
        Assert.Equal("Apply suggestion from @alice on app.cs\n\nCo-authored-by: Alice Smith <42+alice@users.noreply.github.com>",
            Suggestions.CommitMessage([one]));

        var bot = new SuggestedChange("b.cs", 1, 1, ["y"], new PrUser("coderabbitai", null) { IsBot = true });
        Assert.Equal("Apply suggestions from code review\n\nCo-authored-by: Alice Smith <42+alice@users.noreply.github.com>",
            Suggestions.CommitMessage([one, bot, one]));
        Assert.Equal("Apply suggestion from @alice on app.cs", Suggestions.CommitMessage([one], committer: "Alice"));
    }

    [Fact]
    public async Task Commits_suggestions_on_top_of_the_head_without_touching_the_worktree()
    {
        using var repo = new TestRepo();
        repo.Git("config", "core.autocrlf", "false");
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "a.txt"), [.. bom, .. "one\r\ntwo\r\nthree\r\nfour\r\n"u8.ToArray()]);
        repo.Write("dir/b.txt", "keep\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "start");
        var head = repo.Git("rev-parse", "HEAD");

        var sha = await Suggestions.CommitAsync(repo.Root, head,
            [
                new SuggestedChange("a.txt", 1, 1, ["ONE"], Alice),
                new SuggestedChange("a.txt", 3, 4, ["THREE-FOUR"], Alice),
                new SuggestedChange("dir/b.txt", 1, 1, ["kept", "more"], Alice),
            ], "Apply suggestions from code review");

        Assert.Equal(head, repo.Git("rev-parse", sha + "^"));
        Assert.Equal("Apply suggestions from code review", repo.Git("log", "-1", "--format=%s", sha));
        var saved = Path.Combine(repo.Root, "..", Guid.NewGuid().ToString("N"));
        await Git.GitCli.SaveBlobAsync(repo.Root, repo.Git("rev-parse", sha + ":a.txt"), saved);
        var bytes = await File.ReadAllBytesAsync(saved);
        Assert.Equal([.. bom, .. "ONE\r\ntwo\r\nTHREE-FOUR\r\n"u8.ToArray()], bytes);
        File.Delete(saved);
        await Git.GitCli.SaveBlobAsync(repo.Root, repo.Git("rev-parse", sha + ":dir/b.txt"), saved);
        Assert.Equal("kept\nmore\n", await File.ReadAllTextAsync(saved));
        File.Delete(saved);
        // Nothing local changed: same branch, clean worktree and index.
        Assert.Equal(head, repo.Git("rev-parse", "HEAD"));
        Assert.Equal("", repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Overlapping_suggestions_are_refused()
    {
        using var repo = new TestRepo();
        var head = repo.Commit("start", "a.txt", "1\n2\n3\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Suggestions.CommitAsync(repo.Root, head,
            [new SuggestedChange("a.txt", 1, 2, ["x"], Alice), new SuggestedChange("a.txt", 2, 3, ["y"], Alice)], "m"));
    }
}
