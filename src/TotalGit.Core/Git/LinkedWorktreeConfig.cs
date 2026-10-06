using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using LibGit2Sharp;

namespace TotalGit.Core.Git;

/// <summary>
/// Opened from a linked worktree, LibGit2Sharp leaves out the repository's own config file (the shared
/// <c>.git/config</c>), so libgit2 works without its settings: <c>core.filemode = false</c> is missed and an
/// executable-bit difference shows as a change git itself doesn't see (and can't stage), and remotes, line-ending
/// and other repository settings are missing too. This adds that file to libgit2's config for the repository, at
/// the repository ("local") level, through libgit2 directly, as LibGit2Sharp has no way to add a config file.
/// </summary>
internal static class LinkedWorktreeConfig
{
    private const uint LocalLevel = 5; // GIT_CONFIG_LEVEL_LOCAL

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RepositoryConfig(out IntPtr config, IntPtr repository);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ConfigAddFileOnDisk(IntPtr config, byte[] path, uint level, IntPtr repository, int force);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigFree(IntPtr config);

    private static readonly Lazy<(RepositoryConfig Get, ConfigAddFileOnDisk Add, ConfigFree Free)?> Native = new(Bind);

    /// <summary>Adds <paramref name="configPath"/> to <paramref name="repo"/>'s config; false when it couldn't.</summary>
    public static bool Add(Repository repo, string configPath)
    {
        if (Native.Value is not { } native || RepositoryPointer(repo) is not { } repoPtr) return false;
        if (native.Get(out var config, repoPtr) != 0) return false;
        try
        {
            var path = Encoding.UTF8.GetBytes(configPath.Replace('\\', '/') + "\0");
            return native.Add(config, path, LocalLevel, repoPtr, 1) == 0;
        }
        finally
        {
            native.Free(config);
        }
    }

    private static (RepositoryConfig, ConfigAddFileOnDisk, ConfigFree)? Bind()
    {
        try
        {
            // The native library LibGit2Sharp itself loads ("git2-<hash>").
            var asm = typeof(Repository).Assembly;
            var name = asm.GetType("LibGit2Sharp.Core.NativeMethods")?
                .GetField("libgit2", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue() as string;
            if (name is null) return null;
            var lib = NativeLibrary.Load(name, asm, null);
            return (Marshal.GetDelegateForFunctionPointer<RepositoryConfig>(NativeLibrary.GetExport(lib, "git_repository_config")),
                Marshal.GetDelegateForFunctionPointer<ConfigAddFileOnDisk>(NativeLibrary.GetExport(lib, "git_config_add_file_ondisk")),
                Marshal.GetDelegateForFunctionPointer<ConfigFree>(NativeLibrary.GetExport(lib, "git_config_free")));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IntPtr? RepositoryPointer(Repository repo)
    {
        var handle = typeof(Repository).GetProperty("Handle", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(repo);
        var asIntPtr = handle?.GetType().GetMethod("AsIntPtr", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return asIntPtr?.Invoke(handle, null) is IntPtr p && p != IntPtr.Zero ? p : null;
    }
}
