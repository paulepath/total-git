using System.Text.RegularExpressions;

namespace TotalGit.Core.Git;

/// <summary>
/// How branches matching a pattern are shown: the icon in place of their prefix, and the folder the sidebar nests
/// them under. Rules are the user's, kept in their settings; the first matching rule wins.
/// </summary>
public sealed class BranchRule
{
    /// <summary>
    /// A name with <c>*</c> (any characters) and <c>?</c> (one character) wildcards, matched ignoring case:
    /// <c>bug/*</c>, <c>release-*</c>, <c>main</c>. A pattern ending in <c>/*</c> also matches the bare name
    /// before the slash (<c>bugs/*</c> gives the <c>bugs</c> branch the same icon).
    /// </summary>
    public string Pattern { get; set; } = "";

    /// <summary>Folder (or branch) the sidebar nests matching branches under, e.g. <c>bugs</c> for <c>bug/*</c>.</summary>
    public string? GroupUnder { get; set; }

    /// <summary>Show the name without the pattern's fixed prefix (<c>fix-login</c> for <c>bug/fix-login</c>).</summary>
    public bool HidePrefix { get; set; } = true;

    /// <summary>Icon id (<c>builtin:bug</c>, <c>lucide:rocket</c>, <c>custom:file.png</c>), or null for none.</summary>
    public string? Icon { get; set; }

    /// <summary>Colour for a recolourable icon (<c>#E5A33B</c>), or null for the icon's own.</summary>
    public string? IconColor { get; set; }

    /// <summary>
    /// Matching branches are main lines (main, features, bugs…): the graph draws their line thicker. Null (rules
    /// saved before this setting existed) means the built-in main, features and bugs icons decide.
    /// </summary>
    public bool? MainLine { get; set; }

    /// <summary>Whether matching branches are drawn as main lines, with <see cref="MainLine"/> unset falling back to the icon.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsMainLine => MainLine ?? Icon is "builtin:main" or "builtin:features" or "builtin:bugs";

    public BranchRule Clone() => (BranchRule)MemberwiseClone();

    /// <summary>The part of the pattern before its first wildcard ("bug/" for "bug/*").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FixedPrefix
    {
        get
        {
            var i = Pattern.IndexOfAny(['*', '?']);
            return i < 0 ? "" : Pattern[..i];
        }
    }

    /// <summary>A pattern like "bug/*" whose bare name ("bug") the rule also covers.</summary>
    internal string? BareName => Pattern.EndsWith("/*", StringComparison.Ordinal) && Pattern.IndexOfAny(['*', '?']) == Pattern.Length - 1
        ? Pattern[..^2]
        : null;
}

/// <summary>The outcome of matching a branch name against the rules.</summary>
/// <param name="Rule">The matching rule, or null.</param>
/// <param name="ShortName">The name to show (without the prefix when the rule hides it).</param>
/// <param name="IsBareName">The name matched as a pattern's bare name ("bugs" for "bugs/*"), not through its wildcard.</param>
public readonly record struct BranchMatch(BranchRule? Rule, string ShortName, bool IsBareName);

/// <summary>An ordered list of <see cref="BranchRule"/>s.</summary>
public sealed class BranchRuleSet
{
    private readonly List<(BranchRule Rule, Regex Regex)> _rules;

    public BranchRuleSet(IEnumerable<BranchRule> rules)
    {
        _rules = rules.Where(r => !string.IsNullOrWhiteSpace(r.Pattern)).Select(r => (r, ToRegex(r.Pattern))).ToList();
    }

    public IReadOnlyList<BranchRule> Rules => _rules.Select(r => r.Rule).ToList();

    /// <summary>The rules in use by the app (the defaults until the user's are loaded).</summary>
    public static BranchRuleSet Current { get; set; } = new(Defaults());

    /// <summary>
    /// The rules everyone starts with: the feature/bug/hot-fix conventions with their icons, main and master, and
    /// bug/* and feature/* nested under the bugs and features branches.
    /// </summary>
    public static List<BranchRule> Defaults() =>
    [
        new() { Pattern = "main", Icon = "builtin:main", MainLine = true },
        new() { Pattern = "master", Icon = "builtin:main", MainLine = true },
        new() { Pattern = "feature/*", Icon = "builtin:feature", GroupUnder = "features" },
        new() { Pattern = "features/*", Icon = "builtin:features", MainLine = true },
        new() { Pattern = "bug/*", Icon = "builtin:bug", GroupUnder = "bugs" },
        new() { Pattern = "bugfix/*", Icon = "builtin:bug" },
        new() { Pattern = "bugs/*", Icon = "builtin:bugs", MainLine = true },
        new() { Pattern = "hot-fix/*", Icon = "builtin:hot-fix" },
        new() { Pattern = "hotfix/*", Icon = "builtin:hot-fix" },
    ];

    public BranchMatch Match(string? name)
    {
        if (string.IsNullOrEmpty(name)) return new BranchMatch(null, name ?? "", false);
        foreach (var (rule, regex) in _rules)
        {
            if (regex.IsMatch(name))
            {
                var prefix = rule.FixedPrefix;
                var shortName = rule.HidePrefix && prefix.Length > 0 && name.Length > prefix.Length ? name[prefix.Length..] : name;
                return new BranchMatch(rule, shortName, false);
            }
            if (rule.BareName is { } bare && string.Equals(name, bare, StringComparison.OrdinalIgnoreCase))
                return new BranchMatch(rule, name, true);
        }
        return new BranchMatch(null, name, false);
    }

    /// <summary>
    /// Where the sidebar puts a branch: the folders above it and the label of its row. Branches whose rule groups
    /// them go under that group (<c>bug/fix-login</c> → <c>bugs</c> › <c>fix-login</c>); the rest nest by their
    /// own slashes (<c>hot-fix/x</c> → <c>hot-fix</c> › <c>x</c>).
    /// </summary>
    public (IReadOnlyList<string> Folders, string Label) Place(string name)
    {
        var match = Match(name);
        string[] parts;
        if (match is { Rule.GroupUnder: { Length: > 0 } group, IsBareName: false })
        {
            var rest = match.Rule.HidePrefix ? match.ShortName : name;
            parts = [.. group.Split('/', StringSplitOptions.RemoveEmptyEntries), .. rest.Split('/')];
        }
        else
        {
            parts = name.Split('/');
        }
        return (parts[..^1], parts[^1]);
    }

    /// <summary>
    /// How a new branch made from <paramref name="from"/> should start, so it lands next to it: "bug/" from the bugs
    /// branch (bug/* is grouped under it) or from bug/x, "release-" from release-1, "team/" from team/x; empty otherwise.
    /// </summary>
    public string PrefixForNewBranch(string? from)
    {
        if (string.IsNullOrEmpty(from)) return "";
        // A branch other branches are grouped under: new ones join that group.
        var grouping = _rules.Select(r => r.Rule).FirstOrDefault(r =>
            string.Equals(r.GroupUnder, from, StringComparison.OrdinalIgnoreCase) && r.FixedPrefix.Length > 0);
        if (grouping is not null) return grouping.FixedPrefix;
        var match = Match(from);
        if (match.Rule is { } rule && !match.IsBareName && rule.FixedPrefix.Length > 0) return rule.FixedPrefix;
        if (match.IsBareName && match.Rule!.BareName is { } bare) return bare + "/";
        var slash = from.LastIndexOf('/');
        return slash > 0 ? from[..(slash + 1)] : "";
    }

    /// <summary>The rule whose icon a sidebar folder shows: the one for branches inside it ("bugs" → bugs/*).</summary>
    public BranchRule? ForFolder(string path)
    {
        var inside = Match(path + "/x");
        if (inside.Rule is { } r && !inside.IsBareName) return r;
        // A group folder ("features") takes the icon of the rule naming it as its group.
        return _rules.Select(x => x.Rule).FirstOrDefault(x => string.Equals(x.GroupUnder, path, StringComparison.OrdinalIgnoreCase));
    }

    private static Regex ToRegex(string pattern)
    {
        var body = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        // A trailing wildcard needs at least one character: "feature/*" doesn't match "feature/".
        if (body.EndsWith(".*", StringComparison.Ordinal)) body = body[..^2] + ".+";
        return new Regex($"^{body}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
