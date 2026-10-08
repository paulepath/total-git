using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TotalGit.Core.Git;

public sealed record GitResult(int ExitCode, string StdOut, string StdErr);

public class GitCommandException(string command, GitResult result)
    : Exception(string.IsNullOrWhiteSpace(result.StdErr) ? $"git {command} failed with exit code {result.ExitCode}" : result.StdErr.Trim())
{
    public string Command { get; } = command;
    public GitResult Result { get; } = result;
}

/// <summary>Thrown when git refuses a checkout because another worktree already has the branch.</summary>
public sealed partial class BranchInUseException(string command, GitResult result, string worktreePath)
    : GitCommandException(command, result)
{
    public string WorktreePath { get; } = worktreePath;

    [GeneratedRegex(@"(?:used by|checked out at|checked out in).*?worktree at '(?<path>[^']+)'|already (?:used by|checked out at) '(?<path>[^']+)'")]
    internal static partial Regex Pattern();
}

/// <summary>
/// Runs the git command-line client. Used for every write operation so behaviour (credential
/// helpers, hooks, relative worktrees) matches what the user gets in a terminal.
/// </summary>
public static class GitCli
{
    public static string Executable { get; set; } = "git";

    /// <summary>Copies a blob without decoding it as text (discard backups include binary index contents).</summary>
    public static async Task SaveBlobAsync(string worktree, string sha, string destination)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = worktree, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var arg in (string[])["cat-file", "blob", sha]) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start git.");
        var error = process.StandardError.ReadToEndAsync();
        await using (var output = File.Create(destination))
            await process.StandardOutput.BaseStream.CopyToAsync(output);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new GitCommandException("cat-file blob", new(process.ExitCode, "", await error));
    }

    public static async Task<GitResult> RunAsync(
        string workingDirectory,
        IEnumerable<string> args,
        string? stdin = null,
        bool throwOnError = true,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var argList = args.ToList();
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in (string[])["-c", "core.quotepath=off", "-c", "color.ui=false"]) psi.ArgumentList.Add(a);
        foreach (var a in argList) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (env is not null)
            foreach (var (key, value) in env) psi.Environment[key] = value;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start git.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        if (stdin is not null)
        {
            await using var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false));
            await writer.WriteAsync(stdin);
        }
        else
        {
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(ct);
        var result = new GitResult(process.ExitCode, await stdoutTask, await stderrTask);

        if (throwOnError && result.ExitCode != 0)
        {
            var command = string.Join(' ', argList);
            var inUse = BranchInUseException.Pattern().Match(result.StdErr);
            if (inUse.Success) throw new BranchInUseException(command, result, inUse.Groups["path"].Value);
            throw new GitCommandException(command, result);
        }
        return result;
    }

    public static Task<GitResult> RunAsync(string workingDirectory, params string[] args) =>
        RunAsync(workingDirectory, (IEnumerable<string>)args);
}
