using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public class BranchCategoryTests
{
    [Theory]
    [InlineData("feature/e4-2330-x", BranchKind.Feature, "e4-2330-x")]
    [InlineData("Feature/a", BranchKind.Feature, "a")]
    [InlineData("bug/e4-2108-hide", BranchKind.Bug, "e4-2108-hide")]
    [InlineData("bugfix/a", BranchKind.Bug, "a")]
    [InlineData("hot-fix/sync-resolved-ref", BranchKind.HotFix, "sync-resolved-ref")]
    [InlineData("hotfix/a", BranchKind.HotFix, "a")]
    [InlineData("feature/team/a", BranchKind.Feature, "team/a")]
    [InlineData("features/a", BranchKind.Features, "a")]
    [InlineData("bugs/a", BranchKind.Bugs, "a")]
    [InlineData("main", BranchKind.Main, "main")]
    [InlineData("master", BranchKind.Main, "master")]
    [InlineData("mainline", BranchKind.Other, "mainline")]
    [InlineData("features", BranchKind.Features, "features")]
    [InlineData("bugs", BranchKind.Bugs, "bugs")]
    [InlineData("feature", BranchKind.Feature, "feature")]
    [InlineData("hot-fix", BranchKind.HotFix, "hot-fix")]
    [InlineData("featureless", BranchKind.Other, "featureless")]
    [InlineData("backup/e4-1", BranchKind.Other, "backup/e4-1")]
    [InlineData("feature/", BranchKind.Other, "feature/")]
    public void Classifies_by_prefix(string name, BranchKind kind, string shortName)
    {
        Assert.Equal((kind, shortName), BranchCategory.Classify(name));
    }

    [Fact]
    public void Folders_map_to_kinds()
    {
        Assert.Equal(BranchKind.Feature, BranchCategory.ForFolder("feature"));
        Assert.Equal(BranchKind.HotFix, BranchCategory.ForFolder("hot-fix"));
        Assert.Equal(BranchKind.Bugs, BranchCategory.ForFolder("bugs"));
        Assert.Equal(BranchKind.Bug, BranchCategory.ForFolder("bug"));
        Assert.Equal(BranchKind.Features, BranchCategory.ForFolder("features"));
        Assert.Equal(BranchKind.Other, BranchCategory.ForFolder("main"));
        Assert.Equal(BranchKind.Other, BranchCategory.ForFolder("backup"));
        Assert.Equal(BranchKind.Other, BranchCategory.Classify(null).Kind);
    }
}
