namespace TotalGit.Core.Git;

/// <summary>Test code, by the usual naming conventions, so a review can set it aside for later.</summary>
public static class TestFiles
{
    private static readonly string[] Folders = ["test", "tests", "__tests__", "__test__", "spec", "specs", "e2e", "testing"];

    public static bool IsTest(string path)
    {
        var parts = path.Split('/');
        var name = parts[^1];
        // Folders: tests/, __tests__/, spec/, and .NET test projects (Foo.Tests/, Foo.UnitTests/).
        foreach (var folder in parts[..^1])
        {
            if (Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return true;
            if (folder.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) || folder.EndsWith("Tests", StringComparison.Ordinal) && folder.Contains('.'))
                return true;
        }
        var stem = Path.GetFileNameWithoutExtension(name);
        // foo.test.ts, foo.spec.js, foo.test.tsx …
        if (stem.EndsWith(".test", StringComparison.OrdinalIgnoreCase) || stem.EndsWith(".spec", StringComparison.OrdinalIgnoreCase)) return true;
        // FooTests.cs, FooTest.java, FooSpec.scala
        if (stem.Length > 4 && (stem.EndsWith("Tests", StringComparison.Ordinal) || stem.EndsWith("Test", StringComparison.Ordinal) || stem.EndsWith("Spec", StringComparison.Ordinal))
            && char.IsLower(stem[^(stem.EndsWith("Tests", StringComparison.Ordinal) ? 6 : 5)]))
            return true;
        // test_foo.py, foo_test.py, foo_test.go
        return stem.StartsWith("test_", StringComparison.OrdinalIgnoreCase) || stem.EndsWith("_test", StringComparison.OrdinalIgnoreCase);
    }
}
