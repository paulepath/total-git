using System.Text;

namespace TotalGit.Core.Git;

public static class GitIgnore
{
    /// <summary>Escapes a literal path or name for use in a .gitignore rule (wildcards, leading # or !, trailing spaces).</summary>
    public static string Escape(string literal)
    {
        var sb = new StringBuilder(literal.Length);
        for (var i = 0; i < literal.Length; i++)
        {
            var c = literal[i];
            var trailingSpace = c == ' ' && literal.AsSpan(i).Trim(' ').IsEmpty;
            if (c is '*' or '?' or '[' or '\\' || trailingSpace || (i == 0 && c is '#' or '!')) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
