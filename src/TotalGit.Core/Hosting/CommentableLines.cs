using TotalGit.Core.Git;

namespace TotalGit.Core.Hosting;

/// <summary>
/// The lines of a file's diff that a pull request line comment can go on. GitHub only places a comment on a line in
/// its own diff of the file, which shows each change with <see cref="HostContext"/> lines either side, so in the
/// whole-file view most unchanged lines can't take one (GitHub says "could not be resolved").
/// </summary>
public static class CommentableLines
{
    public const int HostContext = 3;

    /// <summary>For each line of <paramref name="lines"/>, whether a comment can go on it.</summary>
    public static bool[] Build(IReadOnlyList<DiffLine> lines)
    {
        var result = new bool[lines.Count];
        // Unchanged lines since the last change (going down), then until the next one (going up).
        var since = int.MaxValue;
        for (var i = 0; i < lines.Count; i++)
        {
            switch (lines[i].Kind)
            {
                case DiffLineKind.Added or DiffLineKind.Removed:
                    result[i] = true;
                    since = 0;
                    break;
                case DiffLineKind.Context:
                    if (since != int.MaxValue) since++;
                    result[i] = since <= HostContext;
                    break;
                case DiffLineKind.Hunk:
                    since = int.MaxValue; // a new hunk is a gap in the file
                    break;
            }
        }
        var until = int.MaxValue;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            switch (lines[i].Kind)
            {
                case DiffLineKind.Added or DiffLineKind.Removed:
                    until = 0;
                    break;
                case DiffLineKind.Context:
                    if (until != int.MaxValue) until++;
                    result[i] |= until <= HostContext;
                    break;
                case DiffLineKind.Hunk:
                    until = int.MaxValue;
                    break;
            }
        }
        return result;
    }
}
