using System.Globalization;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace TotalGit.Core.Git;

/// <summary>A run of a line drawn in one colour: <paramref name="Argb"/> as 0xAARRGGBB.</summary>
public readonly record struct SyntaxSpan(int Start, int Length, uint Argb, bool Bold, bool Italic);

/// <summary>
/// Syntax colours for a file's lines, from the VS Code TextMate grammar for its type and the Dark+ theme. Lines are
/// tokenized in order with the state carried between them, so multi-line comments and strings colour correctly.
/// </summary>
public sealed class SyntaxHighlighter
{
    /// <summary>Lines longer than this are left plain (and the state is reset after them).</summary>
    public const int MaxLineLength = 2000;

    /// <summary>Files with more lines than this aren't coloured at all.</summary>
    public const int MaxLines = 20000;

    private static readonly TimeSpan LineTimeLimit = TimeSpan.FromMilliseconds(50);
    private static readonly IReadOnlyList<SyntaxSpan> Plain = [];

    // Types the bundled grammars don't claim but are XML (MSBuild, XAML and friends).
    private static readonly HashSet<string> XmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".vbproj", ".fsproj", ".proj", ".props", ".targets", ".slnx", ".axaml", ".xaml", ".resx",
        ".config", ".nuspec", ".manifest", ".runsettings", ".ruleset", ".pubxml",
    };

    private static readonly Dictionary<string, string> FileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = ".dockerfile",
        ["Makefile"] = ".mk",
        [".gitignore"] = ".gitignore",
        [".gitattributes"] = ".gitattributes",
        [".editorconfig"] = ".ini",
    };

    private static readonly Lock Gate = new();
    private static RegistryOptions? _options;
    private static Registry? _registry;
    private static Theme? _theme;
    private static readonly Dictionary<string, IGrammar?> Grammars = [];
    private static readonly Dictionary<int, uint> Colours = [];

    private readonly IGrammar _grammar;

    private SyntaxHighlighter(IGrammar grammar) => _grammar = grammar;

    /// <summary>The highlighter for a file's type, or null when there's no grammar for it.</summary>
    public static SyntaxHighlighter? ForPath(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            var ext = FileNames.TryGetValue(name, out var mapped) ? mapped
                : name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase) ? ".dockerfile"
                : Path.GetExtension(name);
            if (string.IsNullOrEmpty(ext)) return null;
            if (XmlExtensions.Contains(ext)) ext = ".xml";

            lock (Gate)
            {
                if (Grammars.TryGetValue(ext, out var cached)) return cached is null ? null : new SyntaxHighlighter(cached);
                var registry = EnsureRegistry();
                var scope = ScopeFor(ext);
                var grammar = scope is null ? null : registry.LoadGrammar(scope);
                Grammars[ext] = grammar;
                return grammar is null ? null : new SyntaxHighlighter(grammar);
            }
        }
        catch (Exception)
        {
            // A missing native regex library or a broken grammar: no colours, not a crash.
            return null;
        }
    }

    private static Registry EnsureRegistry()
    {
        if (_registry is not null) return _registry;
        _options = new RegistryOptions(ThemeName.DarkPlus);
        _registry = new Registry(_options);
        _theme = _registry.GetTheme();
        return _registry;
    }

    private static string? ScopeFor(string ext)
    {
        var language = _options!.GetLanguageByExtension(ext);
        if (language is not null) return _options.GetScopeByLanguageId(language.Id);
        return _options.GetScopeByExtension(ext);
    }

    /// <summary>
    /// The colours for each line (an empty list for a line drawn plain). The state starts afresh at each index in
    /// <paramref name="restartAt"/>, e.g. where a diff's hunks start, since the lines before it aren't the ones above it.
    /// </summary>
    public IReadOnlyList<SyntaxSpan>[] Highlight(IReadOnlyList<string> lines, ISet<int>? restartAt = null,
        CancellationToken cancellationToken = default)
    {
        var result = new IReadOnlyList<SyntaxSpan>[lines.Count];
        Array.Fill(result, Plain);
        if (lines.Count > MaxLines) return result;

        try
        {
            // A grammar keeps compiled rules as it goes, so one file at a time.
            lock (_grammar)
            {
                IStateStack? state = null;
                for (var i = 0; i < lines.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (restartAt?.Contains(i) == true) state = null;
                    var line = lines[i];
                    if (line.Length > MaxLineLength)
                    {
                        state = null;
                        continue;
                    }
                    if (line.Length == 0)
                    {
                        // Still tokenized: an empty line can end or carry on a block.
                        state = _grammar.TokenizeLine2(line, state, LineTimeLimit).RuleStack;
                        continue;
                    }

                    var tokenized = _grammar.TokenizeLine2(line, state, LineTimeLimit);
                    state = tokenized.RuleStack;
                    result[i] = Spans(tokenized.Tokens, line.Length);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Whatever was coloured before the engine failed stays; the rest is plain.
        }
        return result;
    }

    /// <summary>
    /// The colours for each line of a diff. The old side (context and removed lines) and the new side (context and
    /// added lines) are each read as one file, so a removed line is coloured among the lines it used to sit with;
    /// unless the diff has the whole file, each hunk starts afresh.
    /// </summary>
    public IReadOnlyList<SyntaxSpan>[] HighlightDiff(IReadOnlyList<DiffLine> lines, bool wholeFile,
        CancellationToken cancellationToken = default)
    {
        var result = new IReadOnlyList<SyntaxSpan>[lines.Count];
        Array.Fill(result, Plain);
        foreach (var side in new[] { DiffLineKind.Removed, DiffLineKind.Added })
        {
            var indexes = new List<int>();
            var text = new List<string>();
            var restarts = new HashSet<int>();
            for (var i = 0; i < lines.Count; i++)
            {
                var kind = lines[i].Kind;
                if (kind == DiffLineKind.Hunk && !wholeFile) restarts.Add(text.Count);
                if (kind != DiffLineKind.Context && kind != side) continue;
                indexes.Add(i);
                text.Add(lines[i].Text);
            }
            var spans = Highlight(text, restarts, cancellationToken);
            for (var j = 0; j < indexes.Count; j++)
            {
                // Context lines are on both sides; they take the new side's colours.
                if (side == DiffLineKind.Removed && lines[indexes[j]].Kind == DiffLineKind.Context) continue;
                result[indexes[j]] = spans[j];
            }
        }
        return result;
    }

    /// <summary>Spans from the encoded tokens (start, metadata pairs); the default text colour isn't a span.</summary>
    private static IReadOnlyList<SyntaxSpan> Spans(int[] tokens, int length)
    {
        var spans = new List<SyntaxSpan>(tokens.Length / 2);
        for (var t = 0; t < tokens.Length; t += 2)
        {
            var start = tokens[t];
            var end = t + 2 < tokens.Length ? tokens[t + 2] : length;
            if (start >= length || end <= start) continue;
            var meta = tokens[t + 1];
            var foreground = EncodedTokenAttributes.GetForeground(meta);
            var style = EncodedTokenAttributes.GetFontStyle(meta);
            var bold = style != FontStyle.NotSet && style.HasFlag(FontStyle.Bold);
            var italic = style != FontStyle.NotSet && style.HasFlag(FontStyle.Italic);
            // Colour 1 is the theme's default foreground: drawn in the view's own text colour.
            if (foreground <= 1 && !bold && !italic) continue;
            var argb = foreground <= 1 ? 0u : Colour(foreground);
            if (spans.Count > 0 && spans[^1] is var last && last.Start + last.Length == start
                && last.Argb == argb && last.Bold == bold && last.Italic == italic)
                spans[^1] = last with { Length = last.Length + end - start };
            else
                spans.Add(new SyntaxSpan(start, Math.Min(end, length) - start, argb, bold, italic));
        }
        return spans;
    }

    private static uint Colour(int id)
    {
        lock (Colours)
        {
            if (Colours.TryGetValue(id, out var argb)) return argb;
            argb = Parse(_theme!.GetColor(id));
            Colours[id] = argb;
            return argb;
        }
    }

    private static uint Parse(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return 0;
        var digits = hex[1..];
        if (!uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return 0;
        return digits.Length switch
        {
            6 => 0xFF000000 | v,
            8 => (v << 24) | (v >> 8), // #RRGGBBAA
            3 => 0xFF000000 | Expand(v),
            _ => 0,
        };

        static uint Expand(uint rgb) =>
            ((rgb >> 8 & 0xF) * 0x11 << 16) | ((rgb >> 4 & 0xF) * 0x11 << 8) | (rgb & 0xF) * 0x11;
    }
}
