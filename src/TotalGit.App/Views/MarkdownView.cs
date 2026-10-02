using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using TotalGit.App.Services;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace TotalGit.App.Views;

/// <summary>
/// Shows GitHub-flavoured Markdown (pull request descriptions and comments) as formatted text: headings, lists,
/// quotes, code, tables and links. The HTML that bots put in comments is tidied: comments are dropped,
/// &lt;details&gt; becomes a collapsible section, images show their alt text, and other tags keep their text.
/// </summary>
public sealed partial class MarkdownView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        .UseTaskLists()
        .UseEmphasisExtras()
        .Build();

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, monospace");
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#C9CED6"));
    private static readonly IBrush HeadingBrush = new SolidColorBrush(Color.Parse("#EEF0F2"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#8A9099"));
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#5AA9F2"));
    private static readonly IBrush CodeBackground = new SolidColorBrush(Color.Parse("#161A1F"));
    private static readonly IBrush InlineCodeBackground = new SolidColorBrush(Color.Parse("#2B3038"));
    private static readonly IBrush QuoteBar = new SolidColorBrush(Color.Parse("#3A4048"));
    private static readonly IBrush RuleBrush = new SolidColorBrush(Color.Parse("#30353C"));

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Render());
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Base text size; headings and code scale from it.</summary>
    public double BaseFontSize { get; set; } = 12.5;

    private void Render()
    {
        var text = Markdown;
        if (string.IsNullOrWhiteSpace(text))
        {
            Content = null;
            return;
        }
        var root = new StackPanel { Spacing = 8 };
        try
        {
            var doc = Markdig.Markdown.Parse(StripHtmlComments(text), Pipeline);
            // <details> sections open in one HTML block and close in a later one: keep a stack of where blocks go.
            var containers = new Stack<Panel>();
            containers.Push(root);
            foreach (var block in doc) AddBlock(block, containers);
        }
        catch (Exception)
        {
            // Never lose the text over a formatting problem.
            root.Children.Clear();
            root.Children.Add(Paragraph([new Run(text)]));
        }
        Content = root;
    }

    // ------------------------------------------------------------------ blocks

    private void AddBlock(MdBlock block, Stack<Panel> containers)
    {
        var target = containers.Peek();
        switch (block)
        {
            case HtmlBlock html:
                AddHtmlBlock(html, containers);
                break;
            case HeadingBlock heading:
                var size = heading.Level switch { 1 => 1.5, 2 => 1.3, 3 => 1.15, _ => 1.05 };
                var h = Paragraph(Inlines(heading.Inline));
                h.FontSize = BaseFontSize * size;
                h.FontWeight = FontWeight.SemiBold;
                h.Foreground = HeadingBrush;
                h.Margin = new Thickness(0, 4, 0, 0);
                target.Children.Add(h);
                break;
            case ParagraphBlock paragraph:
                target.Children.Add(Paragraph(Inlines(paragraph.Inline)));
                break;
            case ListBlock list:
                target.Children.Add(List(list));
                break;
            case QuoteBlock quote:
                var inner = new StackPanel { Spacing = 6 };
                var quoteStack = new Stack<Panel>();
                quoteStack.Push(inner);
                foreach (var child in quote) AddBlock(child, quoteStack);
                target.Children.Add(new Border
                {
                    BorderBrush = QuoteBar,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 2, 0, 2),
                    Child = inner,
                });
                break;
            case FencedCodeBlock or CodeBlock:
                target.Children.Add(Code(((LeafBlock)block).Lines.ToString()));
                break;
            case ThematicBreakBlock:
                target.Children.Add(new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 4) });
                break;
            case Table table:
                target.Children.Add(TableOf(table));
                break;
            case LinkReferenceDefinitionGroup:
                break;
            case ContainerBlock container:
                foreach (var child in container) AddBlock(child, containers);
                break;
            case LeafBlock leaf when leaf.Inline is not null:
                target.Children.Add(Paragraph(Inlines(leaf.Inline)));
                break;
        }
    }

    [GeneratedRegex(@"<details\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DetailsOpen();

    [GeneratedRegex(@"</details\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex DetailsClose();

    [GeneratedRegex(@"<summary\b[^>]*>(.*?)</summary\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Summary();

    /// <summary>
    /// An HTML block: &lt;details&gt; opens a collapsible section (its &lt;summary&gt; the header) until
    /// &lt;/details&gt;; anything else shows its text, with images as their alt text and links kept.
    /// </summary>
    private void AddHtmlBlock(HtmlBlock html, Stack<Panel> containers)
    {
        var raw = html.Lines.ToString();
        var pos = 0;
        while (pos < raw.Length)
        {
            var open = DetailsOpen().Match(raw, pos);
            var close = DetailsClose().Match(raw, pos);
            var next = new[] { open, close }.Where(m => m.Success).OrderBy(m => m.Index).FirstOrDefault();
            var end = next?.Index ?? raw.Length;
            AddHtmlText(raw[pos..end], containers.Peek());
            if (next is null) break;

            if (next == open)
            {
                pos = open.Index + open.Length;
                var header = "Details";
                var summary = Summary().Match(raw, pos);
                if (summary.Success && raw[pos..summary.Index].Trim().Length == 0)
                {
                    header = HtmlText(summary.Groups[1].Value).Trim();
                    pos = summary.Index + summary.Length;
                }
                var body = new StackPanel { Spacing = 8, Margin = new Thickness(4, 6, 0, 2) };
                containers.Peek().Children.Add(new Expander
                {
                    Header = new TextBlock { Text = header, FontSize = BaseFontSize, Foreground = HeadingBrush, TextWrapping = TextWrapping.Wrap },
                    Content = body,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(8, 4),
                });
                containers.Push(body);
            }
            else
            {
                pos = close.Index + close.Length;
                if (containers.Count > 1) containers.Pop();
            }
        }
    }

    private void AddHtmlText(string html, Panel target)
    {
        var inlines = HtmlInlines(html);
        if (inlines.Count == 0 || inlines.All(i => i is Run r && string.IsNullOrWhiteSpace(r.Text))) return;
        target.Children.Add(Paragraph(inlines));
    }

    private Control List(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 3 };
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var marker = list.IsOrdered ? $"{number++}." : "•";
            var body = new StackPanel { Spacing = 4 };
            var stack = new Stack<Panel>();
            stack.Push(body);
            foreach (var child in item) AddBlock(child, stack);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(new TextBlock
            {
                Text = marker,
                FontSize = BaseFontSize,
                Foreground = MutedBrush,
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = list.IsOrdered ? 18 : 10,
            });
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            panel.Children.Add(row);
        }
        return panel;
    }

    private Control Code(string code) => new Border
    {
        Background = CodeBackground,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 8),
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new SelectableTextBlock
            {
                Text = code.TrimEnd('\n', '\r'),
                FontFamily = Mono,
                FontSize = BaseFontSize - 1,
                Foreground = TextBrush,
            },
        },
    };

    private Control TableOf(Table table)
    {
        var grid = new Grid();
        var columns = table.ColumnDefinitions.Count;
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var r = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var c = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                var inlines = cell.OfType<ParagraphBlock>().SelectMany(p => Inlines(p.Inline)).ToList();
                var text = Paragraph(inlines);
                if (row.IsHeader) text.FontWeight = FontWeight.SemiBold;
                var border = new Border
                {
                    BorderBrush = RuleBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 3),
                    Child = text,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, Math.Min(c++, Math.Max(0, columns - 1)));
                grid.Children.Add(border);
            }
            r++;
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { BorderBrush = RuleBrush, BorderThickness = new Thickness(1, 1, 0, 0), Child = grid, HorizontalAlignment = HorizontalAlignment.Left },
        };
    }

    private SelectableTextBlock Paragraph(IEnumerable<Avalonia.Controls.Documents.Inline> inlines)
    {
        var block = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = BaseFontSize,
            Foreground = TextBrush,
        };
        block.Inlines ??= [];
        foreach (var i in inlines) block.Inlines.Add(i);
        return block;
    }

    // ------------------------------------------------------------------ inlines

    private List<Avalonia.Controls.Documents.Inline> Inlines(ContainerInline? container)
    {
        var result = new List<Avalonia.Controls.Documents.Inline>();
        if (container is null) return result;
        // Inline HTML links (<a href>…</a>) wrap later inlines: collect them into the link while it's open.
        string? openLink = null;
        List<Avalonia.Controls.Documents.Inline>? linkParts = null;
        foreach (var inline in container)
        {
            if (inline is HtmlInline tag)
            {
                var t = tag.Tag;
                if (Regex.IsMatch(t, @"^<a\b", RegexOptions.IgnoreCase))
                {
                    openLink = Attribute(t, "href") ?? "";
                    linkParts = [];
                    continue;
                }
                if (Regex.IsMatch(t, @"^</a\s*>", RegexOptions.IgnoreCase) && linkParts is not null)
                {
                    result.Add(Link(TextOf(linkParts), openLink));
                    openLink = null;
                    linkParts = null;
                    continue;
                }
            }
            var converted = Convert(inline);
            (linkParts ?? result).AddRange(converted);
        }
        if (linkParts is not null) result.AddRange(linkParts);
        return result;
    }

    private IEnumerable<Avalonia.Controls.Documents.Inline> Convert(MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                yield return new Run(literal.Content.ToString());
                break;
            case CodeInline code:
                yield return new Run(code.Content) { FontFamily = Mono, FontSize = BaseFontSize - 1, Background = InlineCodeBackground };
                break;
            case EmphasisInline emphasis:
                var span = new Span();
                foreach (var child in Inlines(emphasis)) span.Inlines.Add(child);
                if (emphasis.DelimiterChar is '~') span.TextDecorations = TextDecorations.Strikethrough;
                else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeight.SemiBold;
                else span.FontStyle = FontStyle.Italic;
                yield return span;
                break;
            case LinkInline { IsImage: true } image:
                var alt = TextOf(Inlines(image));
                yield return new Run(alt.Length > 0 ? $"[{alt}]" : "[image]") { Foreground = MutedBrush };
                break;
            case LinkInline link:
                var text = TextOf(Inlines(link));
                yield return Link(text.Length > 0 ? text : link.Url ?? "", link.Url);
                break;
            case AutolinkInline auto:
                yield return Link(auto.Url, auto.IsEmail ? null : auto.Url);
                break;
            case LineBreakInline brk:
                yield return brk.IsHard ? new LineBreak() : new Run(" ");
                break;
            case HtmlEntityInline entity:
                yield return new Run(entity.Transcoded.ToString());
                break;
            case HtmlInline html:
                foreach (var i in HtmlInlines(html.Tag)) yield return i;
                break;
            case ContainerInline container:
                foreach (var i in Inlines(container)) yield return i;
                break;
        }
    }

    /// <summary>A link: blue and underlined, opening in the browser when clicked (web links only).</summary>
    private Avalonia.Controls.Documents.Inline Link(string text, string? url)
    {
        var web = url is not null && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        if (!web) return new Run(text) { Foreground = LinkBrush };
        var block = new TextBlock
        {
            Text = text,
            Foreground = LinkBrush,
            TextDecorations = TextDecorations.Underline,
            FontSize = BaseFontSize,
            Cursor = new Cursor(StandardCursorType.Hand),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 600,
            // Sit on the line's baseline like the text around it (the container aligns the block's bottom there).
            Margin = new Thickness(0, 0, 0, -BaseFontSize * 0.27),
        };
        ToolTip.SetTip(block, url);
        block.PointerPressed += (_, e) =>
        {
            UrlLauncher.Open(url!);
            e.Handled = true;
        };
        return new InlineUIContainer(block) { BaselineAlignment = BaselineAlignment.Baseline };
    }

    private static string TextOf(IEnumerable<Avalonia.Controls.Documents.Inline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        Run r => r.Text,
        Span s => TextOf(s.Inlines),
        InlineUIContainer { Child: TextBlock t } => t.Text,
        _ => "",
    }));

    // ------------------------------------------------------------------ HTML

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"^(\s*>\s*)\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Alert();

    /// <summary>Drops HTML comments, and turns GitHub's alert markers ("> [!WARNING]") into a bold label.</summary>
    private static string StripHtmlComments(string text)
    {
        var clean = HtmlComment().Replace(text.Replace("\r\n", "\n"), "");
        return Alert().Replace(clean, m =>
        {
            var label = m.Groups[2].Value.ToUpperInvariant() switch
            {
                "WARNING" => "⚠ Warning",
                "CAUTION" => "⛔ Caution",
                "IMPORTANT" => "❗ Important",
                "TIP" => "💡 Tip",
                _ => "ℹ Note",
            };
            return $"{m.Groups[1].Value}**{label}**";
        });
    }

    /// <summary>HTML as inlines: links kept, images as alt text, &lt;br&gt; as line breaks, other tags dropped.</summary>
    private List<Avalonia.Controls.Documents.Inline> HtmlInlines(string html)
    {
        var result = new List<Avalonia.Controls.Documents.Inline>();
        var pos = 0;
        string? href = null;
        var linkText = "";
        foreach (Match tag in AnyTag().Matches(html))
        {
            AddText(html[pos..tag.Index]);
            pos = tag.Index + tag.Length;
            var t = tag.Value;
            if (Regex.IsMatch(t, @"^<a\b", RegexOptions.IgnoreCase))
            {
                href = Attribute(t, "href");
                linkText = "";
            }
            else if (Regex.IsMatch(t, @"^</a", RegexOptions.IgnoreCase) && href is not null)
            {
                if (linkText.Trim().Length > 0) result.Add(Link(linkText.Trim(), href));
                href = null;
            }
            else if (Regex.IsMatch(t, @"^<img\b", RegexOptions.IgnoreCase))
            {
                var alt = Attribute(t, "alt");
                if (href is not null) linkText += alt ?? "";
                else if (!string.IsNullOrWhiteSpace(alt)) result.Add(new Run($"[{alt}]") { Foreground = MutedBrush });
            }
            else if (Regex.IsMatch(t, @"^<br\b", RegexOptions.IgnoreCase) || Regex.IsMatch(t, @"^</(p|div|li|tr|h\d)>", RegexOptions.IgnoreCase))
            {
                result.Add(new LineBreak());
            }
        }
        AddText(html[pos..]);
        // Trailing breaks would leave empty lines.
        while (result.Count > 0 && result[^1] is LineBreak) result.RemoveAt(result.Count - 1);
        return result;

        void AddText(string text)
        {
            var decoded = WebUtility.HtmlDecode(text);
            if (href is not null) linkText += decoded;
            else if (decoded.Length > 0) result.Add(new Run(Regex.Replace(decoded, @"\s+", " ")));
        }
    }

    private static string HtmlText(string html) => WebUtility.HtmlDecode(AnyTag().Replace(html, ""));

    private static string? Attribute(string tag, string name)
    {
        var m = Regex.Match(tag, name + @"\s*=\s*(""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : null;
    }
}
