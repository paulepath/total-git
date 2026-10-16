using TotalGit.Core.Projects;

namespace TotalGit.Core.Tests;

public sealed class VsCodeExtensionsTests
{
    [Theory]
    [InlineData("0.3.46", "0.3.46")]
    [InlineData("1.2.3-beta.1", "1.2.3")]
    [InlineData("1.2.3-win32-x64", "1.2.3")]
    public void Reads_versions(string text, string expected) => Assert.Equal(Version.Parse(expected), VsCodeExtensions.ParseVersion(text));

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3x")]
    [InlineData(null)]
    public void Refuses_what_isnt_a_version(string? text) => Assert.Null(VsCodeExtensions.ParseVersion(text));

    [Fact]
    public void Finds_the_extension_in_extensions_json()
    {
        const string json = """
            [
              {"identifier":{"id":"ms-dotnettools.csharp"},"version":"2.0.0"},
              {"identifier":{"id":"PaulEPath.TotalGit","uuid":"x"},"version":"0.3.46","location":{}},
              {"identifier":{"id":"paulepath.totalgit-extra"},"version":"9.9.9"}
            ]
            """;
        Assert.Equal(new Version(0, 3, 46), VsCodeExtensions.FromExtensionsJson(json, "paulepath.totalgit"));
        Assert.Null(VsCodeExtensions.FromExtensionsJson(json, "someone.else"));
        Assert.Null(VsCodeExtensions.FromExtensionsJson("{}", "paulepath.totalgit"));
    }

    [Fact]
    public void Falls_back_to_the_highest_folder()
    {
        string[] names = ["paulepath.totalgit-0.3.45", "paulepath.totalgit-0.3.102", "paulepath.totalgitx-1.0.0", "other.ext-5.0.0", ".obsolete"];
        Assert.Equal(new Version(0, 3, 102), VsCodeExtensions.FromFolderNames(names, "paulepath.totalgit"));
    }

    [Fact]
    public void Reads_a_folder_and_prefers_its_list()
    {
        var dir = Directory.CreateTempSubdirectory("totalgit-vsext");
        try
        {
            Assert.Null(VsCodeExtensions.InstalledVersion(Path.Combine(dir.FullName, "missing"), "paulepath.totalgit"));

            Directory.CreateDirectory(Path.Combine(dir.FullName, "paulepath.totalgit-0.3.40"));
            Directory.CreateDirectory(Path.Combine(dir.FullName, "paulepath.totalgit-0.3.41"));
            Assert.Equal(new Version(0, 3, 41), VsCodeExtensions.InstalledVersion(dir.FullName, "paulepath.totalgit"));

            // The old folder lingers until VS Code restarts; extensions.json says what's really installed.
            File.WriteAllText(Path.Combine(dir.FullName, "extensions.json"), """[{"identifier":{"id":"paulepath.totalgit"},"version":"0.3.40"}]""");
            Assert.Equal(new Version(0, 3, 40), VsCodeExtensions.InstalledVersion(dir.FullName, "paulepath.totalgit"));

            File.WriteAllText(Path.Combine(dir.FullName, "extensions.json"), "not json");
            Assert.Null(VsCodeExtensions.InstalledVersion(dir.FullName, "paulepath.totalgit"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
