using TotalGit.Core.Projects;

namespace TotalGit.Core.Tests;

public sealed class SolutionFinderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "totalgit-tests", Guid.NewGuid().ToString("N")[..12]);

    public SolutionFinderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Finds_sln_and_slnx_shallowest_first()
    {
        var nested = Touch(Path.Combine("tools", "Tools.sln"));
        var top = Touch("App.slnx");
        Touch("readme.md");

        Assert.Equal([top, nested], SolutionFinder.Find(_root));
    }

    [Fact]
    public void Skips_build_dependency_hidden_and_worktree_folders()
    {
        var real = Touch(Path.Combine("src", "Real.sln"));
        Touch(Path.Combine("node_modules", "pkg", "Dep.sln"));
        Touch(Path.Combine("src", "bin", "Debug", "Copy.sln"));
        Touch(Path.Combine(".worktrees", "other", "App.sln"));
        Touch(Path.Combine(".hidden", "Secret.sln"));

        Assert.Equal([real], SolutionFinder.Find(_root));
    }

    [Fact]
    public void Stops_at_the_depth_limit()
    {
        Touch(Path.Combine("a", "b", "c", "d", "Deep.sln"));
        Assert.Empty(SolutionFinder.Find(_root, maxDepth: 3));
        Assert.Single(SolutionFinder.Find(_root, maxDepth: 4));
    }
}
