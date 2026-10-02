namespace TotalGit.Core.Git;

/// <summary>
/// Files a reviewer rarely needs to read line by line: lock files, generated code and build output. A pull request
/// review starts them folded. Repositories can mark more with <c>linguist-generated</c> in .gitattributes.
/// </summary>
public static class GeneratedFiles
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "packages.lock.json",
        "composer.lock", "Gemfile.lock", "Cargo.lock", "poetry.lock", "Pipfile.lock", "go.sum",
    };

    private static readonly string[] Suffixes =
    [
        ".designer.cs", ".g.cs", ".g.i.cs", ".generated.cs",
        ".min.js", ".min.css", ".js.map", ".css.map", ".snap",
    ];

    private static readonly string[] Folders = ["__snapshots__", "dist", "node_modules"];

    /// <param name="markedGenerated">Paths the repository's .gitattributes mark <c>linguist-generated</c>.</param>
    public static bool IsGenerated(string path, IReadOnlySet<string>? markedGenerated = null)
    {
        if (markedGenerated?.Contains(path) == true) return true;
        var name = Path.GetFileName(path);
        if (Names.Contains(name)) return true;
        if (Suffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return true;
        var folders = path.Split('/')[..^1];
        return folders.Any(f => Folders.Contains(f, StringComparer.OrdinalIgnoreCase));
    }
}
