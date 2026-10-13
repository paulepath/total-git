using System.Diagnostics;

namespace TotalGit.Core.Hosting.GitHub;

/// <summary>An account the GitHub CLI is signed in to on a host; the active one is what gh (and git, through gh) uses.</summary>
public sealed record GitHubAccount(string Login, bool Active);

/// <summary>The GitHub CLI's accounts on a host: listing them, one's token, and making one the active account.</summary>
public interface IGitHubAccounts
{
    /// <summary>The accounts gh is signed in to on <paramref name="host"/> (only those whose sign-in still works).</summary>
    Task<IReadOnlyList<GitHubAccount>> ListAsync(string host, CancellationToken ct);

    Task<string?> TokenAsync(string host, string login, CancellationToken ct);

    /// <summary>Makes <paramref name="login"/> the active account; false when gh refused.</summary>
    Task<bool> SwitchAsync(string host, string login, CancellationToken ct);
}

/// <summary>The GitHub CLI's accounts (<c>gh auth status</c>, <c>gh auth token</c>, <c>gh auth switch</c>).</summary>
public sealed class GhCliAccounts : IGitHubAccounts
{
    public async Task<IReadOnlyList<GitHubAccount>> ListAsync(string host, CancellationToken ct)
    {
        // Only the fields needed: the token never leaves gh. (gh before 2.6x has no --json here: then no accounts.)
        var (ok, output) = await RunAsync(ct, "auth", "status", "--hostname", host, "--json", "hosts",
            "--jq", ".hosts[][] | [.login, (.active | tostring), .state] | @tsv");
        if (!ok) return [];
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(f => f.Length == 3 && f[0].Length > 0 && f[2] == "success")
            .Select(f => new GitHubAccount(f[0], f[1] == "true"))
            .ToList();
    }

    public async Task<string?> TokenAsync(string host, string login, CancellationToken ct)
    {
        var (ok, output) = await RunAsync(ct, "auth", "token", "--hostname", host, "--user", login);
        return ok && output.Length > 0 ? output : null;
    }

    public async Task<bool> SwitchAsync(string host, string login, CancellationToken ct) =>
        (await RunAsync(ct, "auth", "switch", "--hostname", host, "--user", login)).Ok;

    private static async Task<(bool Ok, string Output)> RunAsync(CancellationToken ct, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            if (p is null) return (false, "");
            var output = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
            return (p.ExitCode == 0, (await output).Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                   || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return (false, ""); // gh not installed, or it hung
        }
    }
}
