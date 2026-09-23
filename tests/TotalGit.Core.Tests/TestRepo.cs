using System.Diagnostics;

namespace TotalGit.Core.Tests;

/// <summary>A throwaway repository driven by the git CLI, deleted on dispose.</summary>
public sealed class TestRepo : IDisposable
{
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "totalgit-tests");

    public string Root { get; }

    public TestRepo(bool bare = false, string? path = null)
    {
        Root = path ?? Path.Combine(TempRoot, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
        if (bare)
        {
            Git("init", "-q", "--bare", "-b", "main");
            return;
        }
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test User");
        Git("config", "user.email", "test@example.com");
        Git("config", "commit.gpgsign", "false");
    }

    public string Git(params string[] args) => RunGit(Root, args);

    public static string RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Result.Trim();
    }

    public string Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Writes a file, commits it, and returns the new commit SHA.</summary>
    public string Commit(string message, string file = "file.txt", string? content = null)
    {
        Write(file, content ?? message);
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
        return Git("rev-parse", "HEAD");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
