using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class PathTreeTests
{
    private static IReadOnlyList<PathTreeNode<string>> Build(params string[] paths) => PathTree.Build(paths, p => p);

    [Fact]
    public void Folders_come_first_then_files_sorted_by_name()
    {
        var tree = Build("b.txt", "src/x.cs", "A.txt", "docs/a.md", "docs/b.md");

        Assert.Equal(["docs", "src", "A.txt", "b.txt"], tree.Select(n => n.Name));
        Assert.Equal("docs/", tree[0].FolderPath);
        Assert.Equal(2, tree[0].FileCount);
        Assert.Equal(["a.md", "b.md"], tree[0].Children.Select(c => c.Name));
        Assert.Equal("src/x.cs", tree[1].Children[0].Item);
        Assert.False(tree[2].IsFolder);
    }

    [Fact]
    public void Single_child_folder_chains_are_compacted()
    {
        var tree = Build(
            "apps/partners/src/views/manage/index.jsx",
            "infra/local-env/certs/key.pem",
            "infra/local-env/edge/Pulumi.yaml",
            "infra/local-env/state/.pulumi/history/a.json");

        Assert.Equal(["apps/partners/src/views/manage", "infra/local-env"], tree.Select(n => n.Name));
        Assert.Equal("apps/partners/src/views/manage/", tree[0].FolderPath);
        var env = tree[1];
        Assert.Equal("infra/local-env/", env.FolderPath);
        Assert.Equal(3, env.FileCount);
        Assert.Equal(["certs", "edge", "state/.pulumi/history"], env.Children.Select(c => c.Name));
        Assert.Equal("infra/local-env/state/.pulumi/history/", env.Children[2].FolderPath);
    }

    [Fact]
    public void A_folder_with_its_own_files_is_not_merged_into_its_subfolder()
    {
        var tree = Build("a/one.txt", "a/b/two.txt");

        var a = Assert.Single(tree);
        Assert.Equal("a", a.Name);
        Assert.Equal(["b", "one.txt"], a.Children.Select(c => c.Name));
        Assert.Equal(2, a.FileCount);
    }
}
