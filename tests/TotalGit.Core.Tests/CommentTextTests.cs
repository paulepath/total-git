using TotalGit.Core.Hosting;

namespace TotalGit.Core.Tests;

public sealed class CommentTextTests
{
    [Fact]
    public void Title_is_the_first_bold_line_of_a_bot_review()
    {
        const string body = "_🗄️ Data Integrity & Integration_ | _🟠 Major_ | _🏗️ Heavy lift_\r\n\r\n**Keep the original file bytes in blob storage.**\r\n\r\n`ProcessFileAsync` streams…\r\n\r\n<details>\n<summary>🤖 Prompt for AI Agents</summary>\n\n**Not this**\n</details>\n<!-- fingerprinting:phantom -->";
        Assert.Equal("Keep the original file bytes in blob storage.", CommentText.Title(body));
    }

    [Fact]
    public void Title_falls_back_to_the_first_line_as_plain_text()
    {
        Assert.Equal("Use the helper in utils instead", CommentText.Title("> Use the **helper** in [utils](https://x/y) instead\n\nmore"));
        Assert.Equal("Done", CommentText.Title("```suggestion\nx\n```\n- Done"));
        Assert.Equal("Are we sure nothing still calls old_add?", CommentText.Title("Are we sure nothing still calls `old_add`?"));
        Assert.Equal("Keep snake_case and bold", CommentText.Title("Keep snake_case and _bold_"));
        Assert.Equal("", CommentText.Title("<!-- only a comment -->"));
        Assert.Equal("abcd…", CommentText.Title("abcdefgh", maxLength: 5));
    }
}
