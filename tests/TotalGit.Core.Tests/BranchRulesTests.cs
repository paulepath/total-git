using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class BranchRulesTests
{
    private static readonly BranchRuleSet Defaults = new(BranchRuleSet.Defaults());

    [Theory]
    [InlineData("bug/fix-login", new[] { "bugs" }, "fix-login")]
    [InlineData("feature/team/a", new[] { "features", "team" }, "a")]
    [InlineData("features/a", new[] { "features" }, "a")]
    [InlineData("hot-fix/x", new[] { "hot-fix" }, "x")]
    [InlineData("bugs", new string[0], "bugs")]
    [InlineData("backup/e4/1", new[] { "backup", "e4" }, "1")]
    [InlineData("main", new string[0], "main")]
    public void Places_branches_under_their_group(string name, string[] folders, string label)
    {
        var (f, l) = Defaults.Place(name);
        Assert.Equal(folders, f);
        Assert.Equal(label, l);
    }

    [Fact]
    public void First_matching_rule_wins_and_wildcards_work()
    {
        var rules = new BranchRuleSet(
        [
            new BranchRule { Pattern = "release-*", Icon = "lucide:rocket", IconColor = "#FF8800" },
            new BranchRule { Pattern = "release-?", Icon = "lucide:tag" },
            new BranchRule { Pattern = "e?-*", Icon = "lucide:ticket", HidePrefix = false },
        ]);

        Assert.Equal("lucide:rocket", rules.Match("release-1").Rule!.Icon);
        Assert.Equal("1", rules.Match("release-1").ShortName);
        Assert.Equal("lucide:ticket", rules.Match("e4-2108").Rule!.Icon);
        Assert.Equal("e4-2108", rules.Match("e4-2108").ShortName);
        Assert.Null(rules.Match("release").Rule);
        Assert.Null(rules.Match("e4").Rule);
    }

    [Fact]
    public void A_group_with_the_prefix_kept_nests_the_whole_name()
    {
        var rules = new BranchRuleSet([new BranchRule { Pattern = "bug/*", GroupUnder = "bugs", HidePrefix = false }]);

        var (folders, label) = rules.Place("bug/x");

        Assert.Equal(["bugs", "bug"], folders);
        Assert.Equal("x", label);
    }

    [Fact]
    public void Folders_take_the_icon_of_the_branches_in_them_or_of_the_rule_grouping_into_them()
    {
        var rules = new BranchRuleSet([new BranchRule { Pattern = "fix/*", GroupUnder = "fixes", Icon = "lucide:wrench" }]);

        Assert.Equal("lucide:wrench", rules.ForFolder("fix")!.Icon);
        Assert.Equal("lucide:wrench", rules.ForFolder("fixes")!.Icon);
        Assert.Null(rules.ForFolder("other"));
    }
}
