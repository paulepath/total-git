using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TotalGit.Core.Projects;

/// <summary>
/// Which version of a Visual Studio extension is installed for the user, read from each Visual Studio's per-user
/// folder (%LOCALAPPDATA%\Microsoft\VisualStudio\18.0_1a2b3c4d\Extensions) without running it.
/// </summary>
public static partial class VisualStudioExtensions
{
    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "VisualStudio");

    /// <summary>
    /// The highest version of extension <paramref name="id"/> installed in any Visual Studio 2022 or later under
    /// <paramref name="root"/>, or null. <paramref name="rootSuffix"/> picks a separate instance such as "Exp".
    /// </summary>
    public static Version? InstalledVersion(string root, string id, string rootSuffix = "")
    {
        Version? best = null;
        try
        {
            if (!Directory.Exists(root)) return null;
            foreach (var instance in Directory.EnumerateDirectories(root))
            {
                var match = InstanceFolder().Match(Path.GetFileName(instance));
                if (!match.Success || !string.Equals(match.Groups[1].Value, rootSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                var extensions = Path.Combine(instance, "Extensions");
                if (!Directory.Exists(extensions)) continue;
                foreach (var manifest in Directory.EnumerateFiles(extensions, "extension.vsixmanifest", SearchOption.AllDirectories))
                {
                    if (FromManifest(File.ReadAllText(manifest), id) is { } version && (best is null || version > best)) best = version;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever was readable.
        }
        return best;
    }

    /// <summary>The version in an extension.vsixmanifest when it's extension <paramref name="id"/>'s, else null.</summary>
    public static Version? FromManifest(string xml, string id)
    {
        try
        {
            var identity = XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity");
            return identity is not null && string.Equals((string?)identity.Attribute("Id"), id, StringComparison.OrdinalIgnoreCase)
                ? VsCodeExtensions.ParseVersion((string?)identity.Attribute("Version"))
                : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    // 17.0_1a2b3c4d (2022), 18.0_1a2b3c4d (2026), with any root suffix after it (18.0_1a2b3c4dExp).
    [GeneratedRegex(@"^(?:1[7-9]|[2-9]\d)\.0_[0-9a-f]{8}(\w*)$", RegexOptions.IgnoreCase)]
    private static partial Regex InstanceFolder();
}
