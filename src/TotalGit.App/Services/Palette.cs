using Avalonia.Media;
using Avalonia.Styling;

namespace TotalGit.App.Services;

/// <summary>
/// The app's own colours, each with its dark and light value. The brushes are shared and change colour in place when
/// the theme changes (<see cref="Apply"/>), so views (<c>{x:Static t:Palette.TextMuted}</c>), view models and drawing
/// code that hold one repaint without looking it up again. Fluent's controls follow the theme by themselves.
/// </summary>
public static class Palette
{
    private static readonly List<Entry> Entries = [];

    /// <summary>Raised after the colours change: controls that draw themselves (or cache text) redraw.</summary>
    public static event Action? Changed;

    public static bool IsLight { get; private set; }

    // ------------------------------------------------------------------ surfaces

    /// <summary>The main content: graph, diffs, the review window.</summary>
    public static readonly SolidColorBrush Surface = Make("#1C1F24", "#FFFFFF");
    /// <summary>Side panels, pane headers, the sidebar.</summary>
    public static readonly SolidColorBrush Panel = Make("#23272D", "#F6F8FA");
    /// <summary>Dialog windows.</summary>
    public static readonly SolidColorBrush Dialog = Make("#23272D", "#FFFFFF");
    /// <summary>A list or box set into a dialog.</summary>
    public static readonly SolidColorBrush Well = Make("#1C1F24", "#F6F8FA");
    /// <summary>The repository toolbar, and the selected tab that joins it.</summary>
    public static readonly SolidColorBrush Toolbar = Make("#2B2F36", "#EEF1F4");
    public static readonly SolidColorBrush Splitter = Make("#2B2F36", "#DDE2E7");
    /// <summary>The title row and the tab strip.</summary>
    public static readonly SolidColorBrush Chrome = Make("#16191D", "#E3E7EB");
    public static readonly SolidColorBrush Tab = Make("#1F2328", "#EAEDF0");
    public static readonly SolidColorBrush TabHover = Make("#262A31", "#F3F5F7");
    /// <summary>A bar under a header: the graph's filters, the review window's tabs, the merge tool's buttons.</summary>
    public static readonly SolidColorBrush SubHeader = Make("#1F2328", "#F6F8FA");
    /// <summary>A comment thread's card.</summary>
    public static readonly SolidColorBrush Card = Make("#1F2328", "#FFFFFF");
    /// <summary>A comment's box, raised off its card.</summary>
    public static readonly SolidColorBrush Raised = Make("#262A31", "#FFFFFF");
    public static readonly SolidColorBrush BoxHeader = Make("#2A2F37", "#F6F8FA");
    /// <summary>A quiet box for a line of status text.</summary>
    public static readonly SolidColorBrush SubtleFill = Make("#262A31", "#F6F8FA");
    public static readonly SolidColorBrush Chip = Make("#262A31", "#F6F8FA");
    public static readonly SolidColorBrush EditorToolbar = Make("#22262C", "#F6F8FA");
    /// <summary>Where text is typed (outside Fluent's own text boxes).</summary>
    public static readonly SolidColorBrush Input = Make("#16191D", "#FFFFFF");
    public static readonly SolidColorBrush Popup = Make("#2B3038", "#FFFFFF");
    public static readonly SolidColorBrush TileHover = Make("#2B3038", "#EEF1F4");
    public static readonly SolidColorBrush InlineCode = Make("#2B3038", "#EFF1F3");
    public static readonly SolidColorBrush CodeBlock = Make("#161A1F", "#F6F8FA");
    public static readonly SolidColorBrush Gutter = Make("#20242A", "#F6F8FA");
    /// <summary>The side of a split diff with no line.</summary>
    public static readonly SolidColorBrush DiffFiller = Make("#131518", "#F6F8FA");
    public static readonly SolidColorBrush MergeFiller = Make("#15171B", "#F6F8FA");
    public static readonly SolidColorBrush FillerHatch = Make("#23272D", "#E6E9ED");
    public static readonly SolidColorBrush Overview = Make("#181A1E", "#F6F8FA");
    public static readonly SolidColorBrush DiffGap = Make("#1E2126", "#F6F8FA");
    public static readonly SolidColorBrush ScrollTrack = Make("#1F1F1F", "#F6F8FA");
    public static readonly SolidColorBrush PreviewBox = Make("#1E2227", "#F6F8FA");
    public static readonly SolidColorBrush ZoomOverlay = Make("#E62B2F36", "#F2FFFFFF");

    // ------------------------------------------------------------------ text

    public static readonly SolidColorBrush TextHeading = Make("#EEF0F2", "#1F2328");
    public static readonly SolidColorBrush TextPrimary = Make("#E6E8EB", "#1F2328");
    public static readonly SolidColorBrush TextCode = Make("#DCDFE4", "#1F2328");
    public static readonly SolidColorBrush TextStrong = Make("#D5D8DD", "#31373D");
    public static readonly SolidColorBrush TextStrongAlt = Make("#D5D9DE", "#31373D");
    /// <summary>Running text: markdown, card lines.</summary>
    public static readonly SolidColorBrush TextBody = Make("#C9CED6", "#31373D");
    public static readonly SolidColorBrush TextSoft = Make("#C2C7CE", "#424A53");
    public static readonly SolidColorBrush TextBadge = Make("#B4BAC3", "#4F5862");
    public static readonly SolidColorBrush TextSecondary = Make("#A9AFB8", "#59636E");
    public static readonly SolidColorBrush TextMeta = Make("#969CA5", "#5F6872");
    public static readonly SolidColorBrush TextMuted = Make("#8A9099", "#656D76");
    public static readonly SolidColorBrush TextHeader = Make("#7D848E", "#6E7781");
    public static readonly SolidColorBrush TextHint = Make("#7A818B", "#6E7781");
    public static readonly SolidColorBrush TextDim = Make("#6B727C", "#818B98");
    /// <summary>Text on a coloured banner.</summary>
    public static readonly SolidColorBrush BannerText = Make("#F0F2F5", "#1F2328");
    public static readonly SolidColorBrush BannerCountdown = Make("#66FFFFFF", "#33000000");
    /// <summary>The pending badge's text.</summary>
    public static readonly SolidColorBrush PendingBadgeText = Make("#1C1F24", "#7D4E00");
    /// <summary>Under the selected Write / Preview tab.</summary>
    public static readonly SolidColorBrush TabUnderline = Make("#E8762B", "#FD8C73");

    // ------------------------------------------------------------------ accents

    public static readonly SolidColorBrush Accent = Make("#5AA9F2", "#0969DA");
    public static readonly SolidColorBrush AccentStrong = Make("#3B82F6", "#0969DA");
    public static readonly SolidColorBrush AccentBorder = Make("#3B82C4", "#0969DA");
    public static readonly SolidColorBrush Blue = Make("#2D7BF4", "#0969DA");
    public static readonly SolidColorBrush BranchText = Make("#9DC4EA", "#0550AE");
    public static readonly SolidColorBrush ChipText = Make("#9DB8D6", "#0550AE");
    public static readonly SolidColorBrush InfoText = Make("#C9D8EA", "#0A3069");
    public static readonly SolidColorBrush ReactedText = Make("#C9DDF5", "#0A3069");
    public static readonly SolidColorBrush ChipActiveText = Make("#D6E7F8", "#0A3069");
    public static readonly SolidColorBrush GhostText = Make("#A9C8E8", "#3A6EA5");
    public static readonly SolidColorBrush GhostBorder = Make("#3B5E86", "#80B3E8");
    public static readonly SolidColorBrush OursTitle = Make("#8CC4FF", "#0550AE");
    public static readonly SolidColorBrush HunkText = Make("#7FA8E8", "#0550AE");
    public static readonly SolidColorBrush VsCodeBlue = Make("#3FA0F0", "#0078D4");
    public static readonly SolidColorBrush VisualStudioPurple = Make("#A77BDB", "#865FC5");

    public static readonly SolidColorBrush Success = Make("#4CC38A", "#1A7F37");
    public static readonly SolidColorBrush Green = Make("#2FBF71", "#1A7F37");
    public static readonly SolidColorBrush ResolvedText = Make("#7EE2A0", "#1A7F37");
    public static readonly SolidColorBrush CheckedOutText = Make("#86E3B6", "#116329");
    public static readonly SolidColorBrush TheirsTitle = Make("#7FE0B2", "#116329");
    public static readonly SolidColorBrush TheirsHint = Make("#A3DCC2", "#2D7A50");

    public static readonly SolidColorBrush Pending = Make("#E8B339", "#9A6700");
    public static readonly SolidColorBrush OutdatedText = Make("#F0C35A", "#7D4E00");
    public static readonly SolidColorBrush Warning = Make("#F5C26B", "#9A6700");
    public static readonly SolidColorBrush WarningText = Make("#F5D48B", "#7D4E00");
    public static readonly SolidColorBrush BaseTitle = Make("#F0C27A", "#7D4E00");
    public static readonly SolidColorBrush MergeBase = Make("#E5A33B", "#BF8700");

    public static readonly SolidColorBrush Failure = Make("#E5484D", "#CF222E");
    public static readonly SolidColorBrush Error = Make("#F26B6B", "#D1242F");
    public static readonly SolidColorBrush DangerText = Make("#F47067", "#D1242F");
    public static readonly SolidColorBrush Red = Make("#E5392F", "#CF222E");
    public static readonly SolidColorBrush MergeNotice = Make("#E8A0A0", "#A40E26");

    public static readonly SolidColorBrush Purple = Make("#A371F7", "#8250DF");
    public static readonly SolidColorBrush WorktreeName = Make("#B084F5", "#8250DF");
    public static readonly SolidColorBrush PurpleText = Make("#D9CCFA", "#6639BA");

    public static readonly SolidColorBrush Draft = Make("#5C636D", "#8C959F");
    public static readonly SolidColorBrush Cancelled = Make("#4A5059", "#AFB8C1");
    public static readonly SolidColorBrush WipRing = Make("#A0A7B0", "#8C959F");

    // ------------------------------------------------------------------ fills (badges, chips, tinted boxes)

    public static readonly SolidColorBrush BranchChip = Make("#24303C", "#DDF4FF");
    public static readonly SolidColorBrush TestChip = Make("#2B3340", "#DDF4FF");
    public static readonly SolidColorBrush SidebarChip = Make("#2C3440", "#E7EEF6");
    public static readonly SolidColorBrush SidebarChipActive = Make("#34506E", "#B6D7F5");
    /// <summary>A toggled-on filter or button.</summary>
    public static readonly SolidColorBrush SelectedFill = Make("#1F4E7A", "#DDF4FF");
    public static readonly SolidColorBrush SelectedFillText = Make("#FFFFFF", "#0A3069");
    public static readonly SolidColorBrush SegmentFill = Make("#2A2F36", "#EAEEF2");
    public static readonly SolidColorBrush SegmentActive = Make("#2D5E9E", "#0969DA");
    public static readonly SolidColorBrush SegmentActiveHover = Make("#3570B8", "#0860CA");
    public static readonly SolidColorBrush NeutralChip = Make("#3A4250", "#D8DEE4");
    public static readonly SolidColorBrush Separator = Make("#3A3F47", "#D0D7DE");
    public static readonly SolidColorBrush PurpleFill = Make("#2A2140", "#FBEFFF");
    public static readonly SolidColorBrush ResolvedFill = Make("#1B3524", "#DAFBE1");
    public static readonly SolidColorBrush CheckedOutFill = Make("#123326", "#DAFBE1");
    public static readonly SolidColorBrush AddedLine = Make("#1D3A26", "#E6FFEC");
    public static readonly SolidColorBrush RemovedLine = Make("#3A1D20", "#FFEBE9");
    public static readonly SolidColorBrush AddedLineText = Make("#B5E8C3", "#0F5323");
    public static readonly SolidColorBrush AddedLineSign = Make("#4FBF6B", "#1A7F37");
    public static readonly SolidColorBrush RemovedLineText = Make("#F2B8B5", "#82071E");
    public static readonly SolidColorBrush RemovedLineSign = Make("#F47067", "#CF222E");
    public static readonly SolidColorBrush OkFill = Make("#1F3A2C", "#DAFBE1");
    public static readonly SolidColorBrush ErrorFill = Make("#5A2427", "#FFEBE9");
    public static readonly SolidColorBrush ErrorBanner = Make("#5A2327", "#FFEBE9");
    public static readonly SolidColorBrush InfoBanner = Make("#1F3A5A", "#DDF4FF");
    public static readonly SolidColorBrush OutdatedFill = Make("#3A3018", "#FFF8C5");
    public static readonly SolidColorBrush WarningFill = Make("#4A3A12", "#FFF8C5");
    public static readonly SolidColorBrush OperationBanner = Make("#5B4513", "#FFF1C2");
    public static readonly SolidColorBrush PendingBadge = Make("#E8B339", "#FFF1C2");
    public static readonly SolidColorBrush PendingBorder = Make("#6B5A2A", "#D4A72C");
    public static readonly SolidColorBrush BaseFill = Make("#3A2E17", "#FFF5D6");
    public static readonly SolidColorBrush OursFill = Make("#1B2C40", "#E3F0FF");
    public static readonly SolidColorBrush TheirsFill = Make("#1B3329", "#E3F7EA");
    public static readonly SolidColorBrush BaseActive = Make("#8A6420", "#BF8700");
    public static readonly SolidColorBrush OursActive = Make("#24497A", "#0969DA");
    public static readonly SolidColorBrush TheirsActive = Make("#1F6146", "#1A7F37");
    public static readonly SolidColorBrush AuthorHeader = Make("#1E2D42", "#DDF4FF");
    public static readonly SolidColorBrush AuthorBorder = Make("#2F4F7A", "#B6D7F5");
    public static readonly SolidColorBrush AuthorBadgeBorder = Make("#3F6699", "#54AEFF");
    public static readonly SolidColorBrush ReactedFill = Make("#1F3A5F", "#DDF4FF");
    public static readonly SolidColorBrush ReactedBorder = Make("#2F6FBF", "#54AEFF");
    public static readonly SolidColorBrush PreviewChip = Make("#1F6FB5", "#0969DA");
    public static readonly SolidColorBrush PreviewChipHole = Make("#16191D", "#FFFFFF");
    public static readonly SolidColorBrush ReadyBadge = Make("#2EA043", "#1F883D");

    // Buttons with white text: the same in both themes, a little deeper in the light one.
    public static readonly SolidColorBrush GreenButton = Make("#238636", "#1F883D");
    public static readonly SolidColorBrush GreenButtonHover = Make("#2EA043", "#1A7F37");
    public static readonly SolidColorBrush GreenButtonDark = Make("#1D6F2E", "#116329");
    public static readonly SolidColorBrush ApproveButton = Make("#2FA36B", "#1F883D");
    public static readonly SolidColorBrush ApproveButtonHover = Make("#38B87A", "#1A7F37");
    public static readonly SolidColorBrush RejectButton = Make("#D9474D", "#CF222E");
    public static readonly SolidColorBrush RejectButtonHover = Make("#E5585E", "#A40E26");
    public static readonly SolidColorBrush DangerButton = Make("#B4373C", "#CF222E");

    // ------------------------------------------------------------------ borders

    public static readonly SolidColorBrush Border = Make("#30353C", "#D1D9E0");
    public static readonly SolidColorBrush BorderStrong = Make("#3A404A", "#D1D9E0");
    public static readonly SolidColorBrush BorderControl = Make("#3A4048", "#D1D9E0");
    public static readonly SolidColorBrush BorderPopup = Make("#474E59", "#D1D9E0");
    public static readonly SolidColorBrush BorderInput = Make("#4A505A", "#C4CDD5");
    public static readonly SolidColorBrush BorderBadge = Make("#4A515C", "#C4CDD5");
    public static readonly SolidColorBrush BorderHover = Make("#444B56", "#C4CDD5");
    public static readonly SolidColorBrush RailLine = Make("#353B44", "#D1D9E0");
    public static readonly SolidColorBrush DiffGapBorder = Make("#2A2F36", "#D1D9E0");
    public static readonly SolidColorBrush CommentRing = Make("#16181C", "#FFFFFF");

    // ------------------------------------------------------------------ overlays and tints (drawn over other colours)

    public static readonly SolidColorBrush HoverOverlay = Make("#18FFFFFF", "#0B000000");
    public static readonly SolidColorBrush PillHoverOverlay = Make("#22FFFFFF", "#14000000");
    public static readonly SolidColorBrush FoldChip = Make("#30FFFFFF", "#14000000");
    public static readonly SolidColorBrush RangeFill = Make("#483B82F6", "#2E0969DA");
    public static readonly SolidColorBrush GhostFill = Make("#1C3B82F6", "#120969DA");
    public static readonly SolidColorBrush DropFill = Make("#404CC38A", "#301A7F37");
    /// <summary>Behind a graph node's avatar or branch icon, so transparent images don't take the lane colour.</summary>
    public static readonly SolidColorBrush NodeHole = Make("#000000", "#FFFFFF");
    /// <summary>A branch label in the graph (white on a strong pill in both, dark on a light one).</summary>
    public static readonly SolidColorBrush PillText = Make("#FFFFFF", "#1F2328");
    public static readonly SolidColorBrush MergeLineDivider = Make("#FFFFFF", "#000000", 0.08, 0.08);
    public static readonly SolidColorBrush MergeViewport = Make("#FFFFFF", "#000000", 0.10, 0.08);

    // Diffs: whole lines, the changed words, the gutter beside them.
    public static readonly SolidColorBrush DiffAdded = Make("#2FBF71", "#E6FFEC", 0.10, 1);
    public static readonly SolidColorBrush DiffRemoved = Make("#E5392F", "#FFEBE9", 0.11, 1);
    public static readonly SolidColorBrush DiffAddedWord = Make("#2FBF71", "#ABF2BC", 0.48, 1);
    public static readonly SolidColorBrush DiffRemovedWord = Make("#E5392F", "#FFCECB", 0.52, 1);
    public static readonly SolidColorBrush DiffAddedGutter = Make("#2FBF71", "#CCFFD8", 0.28, 1);
    public static readonly SolidColorBrush DiffRemovedGutter = Make("#E5392F", "#FFD7D5", 0.3, 1);
    public static readonly SolidColorBrush DiffHunk = Make("#2D7BF4", "#DDF4FF", 0.14, 1);
    public static readonly SolidColorBrush DiffSinceReview = Make("#30A371F7", "#1F8250DF");
    public static readonly SolidColorBrush DiffSelection = Make("#3B82F6", "#0969DA", 0.38, 0.22);
    public static readonly SolidColorBrush MarkAdded = Make("#2FBF71", "#2DA44E", 0.85, 1);
    public static readonly SolidColorBrush MarkRemoved = Make("#E5392F", "#E5534B", 0.85, 1);
    public static readonly SolidColorBrush ChangedRow = Make("#4CC38A", "#1A7F37", 0.14, 0.12);

    // ------------------------------------------------------------------ the graph's lanes

    private static readonly Color[] DarkLanes =
        [.. new[] { "#1FB6DC", "#2D7BF4", "#9B3FE0", "#D02DC4", "#E8246F", "#E5392F", "#F07F1F", "#F2C618", "#A3D82C", "#2FBF71" }.Select(Color.Parse)];

    // Deeper, so lines, pills and white text on them hold up on white.
    private static readonly Color[] LightLanes =
        [.. new[] { "#0B98BD", "#2563D9", "#8237C9", "#B526AA", "#D01D5E", "#D1332A", "#D96A10", "#B98F00", "#5E9A12", "#1F9A55" }.Select(Color.Parse)];

    public static IReadOnlyList<Color> Lanes => IsLight ? LightLanes : DarkLanes;

    /// <summary>A lane-coloured row band's opacity, a selected one's, and a branch pill's.</summary>
    public static (double Band, double StrongBand, double Pill) LaneOpacities => IsLight ? (0.09, 0.26, 0.16) : (0.13, 0.55, 0.35);

    // ------------------------------------------------------------------ syntax colours

    // The diff colours code with VS Code's Dark+ theme; on a light background each becomes its Light+ counterpart.
    private static readonly Dictionary<uint, uint> LightSyntax = new()
    {
        [0xFF569CD6] = 0xFF0000FF, // keywords, tags
        [0xFFC586C0] = 0xFFAF00DB, // control keywords
        [0xFFCE9178] = 0xFFA31515, // strings
        [0xFF6A9955] = 0xFF008000, // comments
        [0xFFB5CEA8] = 0xFF098658, // numbers
        [0xFF4EC9B0] = 0xFF267F99, // types
        [0xFFDCDCAA] = 0xFF795E26, // functions
        [0xFF9CDCFE] = 0xFF001080, // variables, attributes
        [0xFF4FC1FF] = 0xFF0070C1, // constants
        [0xFFD16969] = 0xFF811F3F, // regular expressions
        [0xFFD7BA7D] = 0xFFEE0000, // escapes, selectors
        [0xFF808080] = 0xFF800000, // tag punctuation
        [0xFFF44747] = 0xFFCD3131, // invalid
        [0xFFD4D4D4] = 0xFF1F2328, // plain text
        [0xFFC8C8C8] = 0xFF1F2328,
        [0xFF6796E6] = 0xFF0451A5, // headings
        [0xFF608B4E] = 0xFF008000,
        [0xFF646695] = 0xFF811F3F,
    };

    /// <summary>A syntax colour (Dark+) as the current theme draws it.</summary>
    public static uint Syntax(uint argb) => IsLight && LightSyntax.TryGetValue(argb, out var light) ? light : argb;

    // ------------------------------------------------------------------ switching

    /// <summary>Sets every colour for <paramref name="variant"/> (Light, else dark). Call on the UI thread.</summary>
    public static void Apply(ThemeVariant variant)
    {
        var light = variant == ThemeVariant.Light;
        if (light == IsLight && Entries.All(e => e.Brush.Color == (light ? e.Light : e.Dark))) return;
        IsLight = light;
        foreach (var e in Entries)
        {
            e.Brush.Color = light ? e.Light : e.Dark;
            e.Brush.Opacity = light ? e.LightOpacity : e.DarkOpacity;
        }
        Changed?.Invoke();
    }

    private static SolidColorBrush Make(string dark, string light, double darkOpacity = 1, double lightOpacity = 1)
    {
        var entry = new Entry(new SolidColorBrush(Color.Parse(dark), darkOpacity), Color.Parse(dark), Color.Parse(light), darkOpacity, lightOpacity);
        Entries.Add(entry);
        return entry.Brush;
    }

    private sealed record Entry(SolidColorBrush Brush, Color Dark, Color Light, double DarkOpacity, double LightOpacity);
}
