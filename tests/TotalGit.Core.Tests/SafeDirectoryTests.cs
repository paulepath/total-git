using TotalGit.Core.Git;

namespace TotalGit.Core.Tests;

public sealed class SafeDirectoryTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "totalgit-tests", "safe-" + Guid.NewGuid().ToString("N")[..8] + ".gitconfig");

    public SafeDirectoryTests() => Directory.CreateDirectory(Path.GetDirectoryName(_config)!);

    public void Dispose()
    {
        if (File.Exists(_config)) File.Delete(_config);
    }

    [Theory]
    [InlineData("repository path 'E:/richardson/repos/api' is not owned by current user", "E:/richardson/repos/api")]
    [InlineData("fatal: detected dubious ownership in repository at 'D:/code/x'\nTo add an exception...", "D:/code/x")]
    [InlineData("could not find repository at 'E:/x'", null)]
    [InlineData(null, null)]
    public void Finds_the_untrusted_path_in_ownership_errors(string? message, string? expected) =>
        Assert.Equal(expected, SafeDirectory.UntrustedPath(message));

    [Fact]
    public async Task Adds_the_folder_once_with_forward_slashes()
    {
        await SafeDirectory.TrustAsync(@"E:\richardson\repos\api\", _config);
        await SafeDirectory.TrustAsync("E:/richardson/repos/api", _config);

        var result = await GitCli.RunAsync(Path.GetTempPath(), ["config", "--file", _config, "--get-all", "safe.directory"]);
        Assert.Equal(["E:/richardson/repos/api"], result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
