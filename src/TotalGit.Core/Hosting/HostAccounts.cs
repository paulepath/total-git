namespace TotalGit.Core.Hosting;

/// <summary>A switch to the account that can see a repository: <see cref="From"/> is null when it was already active.</summary>
public sealed record AccountSwitch(string Login, string? From);

/// <summary>Moves the hosting service's sign-in to an account that can see a repository.</summary>
public interface IHostAccountSwitcher
{
    /// <summary>
    /// When several accounts are signed in, finds one that can see <paramref name="host"/>'s repository and makes it
    /// the active account (for Total Git, and for git and gh everywhere else too). Null when there's no such account.
    /// </summary>
    Task<AccountSwitch?> UseAccountThatCanSeeAsync(RemoteHostInfo host, CancellationToken ct = default);
}
