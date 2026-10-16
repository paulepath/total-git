using TotalGit.Core.Projects;

namespace TotalGit.Core.Tests;

public sealed class VisualStudioExtensionsTests
{
    private const string Id = "paulepath.TotalGit.VisualStudio";

    private static string Manifest(string id, string version) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
          <Metadata>
            <Identity Id="{id}" Version="{version}" Language="en-US" Publisher="paulepath" />
            <DisplayName>Total Git</DisplayName>
          </Metadata>
        </PackageManifest>
        """;

    [Fact]
    public void Reads_the_version_from_its_manifest()
    {
        Assert.Equal(new Version(0, 3, 47), VisualStudioExtensions.FromManifest(Manifest(Id, "0.3.47"), Id));
        Assert.Null(VisualStudioExtensions.FromManifest(Manifest("someone.Else", "1.2.3"), Id));
        Assert.Null(VisualStudioExtensions.FromManifest("not xml", Id));
    }

    [Fact]
    public void Finds_the_highest_install_and_keeps_instances_apart()
    {
        var root = Directory.CreateTempSubdirectory("totalgit-vsext");
        try
        {
            void Install(string instance, string folder, string id, string version)
            {
                var dir = Path.Combine(root.FullName, instance, "Extensions", folder);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "extension.vsixmanifest"), Manifest(id, version));
            }

            Assert.Null(VisualStudioExtensions.InstalledVersion(root.FullName, Id));

            Install("17.0_1a2b3c4d", "abc.xyz", Id, "0.3.40");
            Install("18.0_8c4cc559", @"paulepath\Total Git\0.3.45", Id, "0.3.45");
            Install("18.0_8c4cc559", "other", "someone.Else", "9.9.9");
            Install("18.0_8c4cc559Exp", "dev", Id, "0.3.99");
            Install("16.0_1a2b3c4d", "old", Id, "1.0.0"); // Visual Studio 2019: not a target
            Install("SettingsBackup_8c4cc559", "x", Id, "2.0.0");

            Assert.Equal(new Version(0, 3, 45), VisualStudioExtensions.InstalledVersion(root.FullName, Id));
            Assert.Equal(new Version(0, 3, 99), VisualStudioExtensions.InstalledVersion(root.FullName, Id, "Exp"));
            Assert.Null(VisualStudioExtensions.InstalledVersion(Path.Combine(root.FullName, "missing"), Id));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
