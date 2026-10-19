using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

/// <summary>
/// The box comments are written in: Write / Preview, a Markdown toolbar that works on the selection, @mentions
/// offered as you type, and a suggested-change block for the commented lines. Ctrl+Enter sends.
/// </summary>
public partial class CommentEditor : UserControl
{
    private CommentEditorViewModel? _vm;
    private readonly DispatcherTimer _mentionTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _mentionRequest;

    /// <summary>Where the "@" being completed starts in the text, while the list is open.</summary>
    private int _mentionStart = -1;

    public CommentEditor()
    {
        InitializeComponent();
        Toolbar.AddHandler(Button.ClickEvent, OnToolbarClick);
        Box.AddHandler(KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Tunnel);
        Box.TextChanged += (_, _) =>
        {
            _mentionTimer.Stop();
            _mentionTimer.Start();
        };
        _mentionTimer.Tick += (_, _) =>
        {
            _mentionTimer.Stop();
            _ = UpdateMentionsAsync();
        };
        Mentions.AddHandler(PointerReleasedEvent, (_, _) => PickMention(), RoutingStrategies.Bubble, handledEventsToo: true);
        SizeChanged += (_, e) => LayOutToolbar(e.NewSize.Width);
    }

    /// <summary>Beside the Write / Preview tabs when there's room, otherwise on a row of its own under them.</summary>
    private void LayOutToolbar(double width)
    {
        var narrow = width < 640;
        Grid.SetRow(Toolbar, narrow ? 1 : 0);
        Grid.SetColumn(Toolbar, narrow ? 0 : 1);
        Grid.SetColumnSpan(Toolbar, narrow ? 2 : 1);
        Toolbar.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;
        SuggestText.IsVisible = width >= 420;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.FocusRequested -= FocusBox;
        _vm = DataContext as CommentEditorViewModel;
        if (_vm is not null) _vm.FocusRequested += FocusBox;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Asked for the cursor before this box was shown (a reply just opened): it goes straight in.
        if (_vm is { WantsFocus: true }) FocusBox();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _mentionTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void FocusBox() => Dispatcher.UIThread.Post(() =>
    {
        // Another copy of this box (the same thread shown elsewhere) is the one being used.
        if (ReviewThreadView.Active is { } active && this.FindAncestorOfType<ReviewThreadView>() is { } card && card != active) return;
        if (_vm is not null) _vm.WantsFocus = false;
        Box.Focus();
        Box.CaretIndex = Box.Text?.Length ?? 0;
        Box.SelectionStart = Box.SelectionEnd = Box.CaretIndex;
    }, DispatcherPriority.Background);

    // ------------------------------------------------------------------ keys

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (MentionPopup.IsOpen)
        {
            var count = Mentions.ItemCount;
            switch (e.Key)
            {
                case Key.Down when count > 0:
                    Mentions.SelectedIndex = (Mentions.SelectedIndex + 1) % count;
                    e.Handled = true;
                    return;
                case Key.Up when count > 0:
                    Mentions.SelectedIndex = (Mentions.SelectedIndex - 1 + count) % count;
                    e.Handled = true;
                    return;
                case Key.Enter or Key.Tab when e.KeyModifiers == KeyModifiers.None:
                    PickMention();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    CloseMentions();
                    e.Handled = true;
                    return;
            }
        }

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    if (_vm?.SubmitCommand is { } submit && submit.CanExecute(null)) submit.Execute(null);
                    e.Handled = true;
                    break;
                case Key.B:
                    Wrap("**", "**", "bold text");
                    e.Handled = true;
                    break;
                case Key.I:
                    Wrap("_", "_", "italic text");
                    e.Handled = true;
                    break;
                case Key.E:
                    Code();
                    e.Handled = true;
                    break;
                case Key.K:
                    Link();
                    e.Handled = true;
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ the toolbar

    private void OnToolbarClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Control source) return;
        var button = source as Button ?? source.FindAncestorOfType<Button>();
        switch (button?.Tag as string)
        {
            case "heading": PrefixLines(_ => "### "); break;
            case "bold": Wrap("**", "**", "bold text"); break;
            case "italic": Wrap("_", "_", "italic text"); break;
            case "quote": PrefixLines(_ => "> "); break;
            case "code": Code(); break;
            case "link": Link(); break;
            case "bullets": PrefixLines(_ => "- "); break;
            case "numbers": PrefixLines(i => $"{i + 1}. "); break;
            case "tasks": PrefixLines(_ => "- [ ] "); break;
            case "mention": Insert(NeedsSpaceBefore() ? " @" : "@"); _ = UpdateMentionsAsync(); break;
            case "suggest": Suggest(); break;
            default: return;
        }
        e.Handled = true;
    }

    private (int Start, int End, string Text) Selection()
    {
        var text = Box.Text ?? "";
        var start = Math.Clamp(Math.Min(Box.SelectionStart, Box.SelectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(Box.SelectionStart, Box.SelectionEnd), 0, text.Length);
        if (start == end) start = end = Math.Clamp(Box.CaretIndex, 0, text.Length);
        return (start, end, text);
    }

    /// <summary>Replaces the selection, then selects <paramref name="selectFrom"/>..<paramref name="selectTo"/> of what was put in.</summary>
    private void Replace(int start, int end, string with, int? selectFrom = null, int? selectTo = null)
    {
        var text = Box.Text ?? "";
        Box.Text = text[..start] + with + text[end..];
        Box.Focus();
        var from = start + (selectFrom ?? with.Length);
        var to = start + (selectTo ?? with.Length);
        Box.SelectionStart = from;
        Box.SelectionEnd = to;
        Box.CaretIndex = to;
    }

    private void Insert(string text)
    {
        var (start, end, _) = Selection();
        Replace(start, end, text);
    }

    private bool NeedsSpaceBefore()
    {
        var (start, _, text) = Selection();
        return start > 0 && !char.IsWhiteSpace(text[start - 1]);
    }

    /// <summary>Wraps the selection (or a placeholder, selected so typing replaces it).</summary>
    private void Wrap(string before, string after, string placeholder)
    {
        var (start, end, text) = Selection();
        var inner = start == end ? placeholder : text[start..end];
        Replace(start, end, before + inner + after, before.Length, before.Length + inner.Length);
    }

    /// <summary>Inline code for part of a line, a fenced block for whole lines.</summary>
    private void Code()
    {
        var (start, end, text) = Selection();
        if (start != end && text[start..end].Contains('\n'))
        {
            var block = "```\n" + text[start..end].TrimEnd('\n') + "\n```\n";
            Replace(start, end, (start > 0 && text[start - 1] != '\n' ? "\n" : "") + block);
        }
        else Wrap("`", "`", "code");
    }

    private void Link()
    {
        var (start, end, text) = Selection();
        var label = start == end ? "text" : text[start..end];
        var inserted = $"[{label}](url)";
        // The url is selected, ready to paste over.
        Replace(start, end, inserted, label.Length + 3, label.Length + 6);
    }

    /// <summary>Puts a prefix before each selected line (or the caret's line).</summary>
    private void PrefixLines(Func<int, string> prefix)
    {
        var (start, end, text) = Selection();
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var lineEnd = text.IndexOf('\n', end);
        if (lineEnd < 0) lineEnd = text.Length;
        var lines = text[lineStart..lineEnd].Split('\n');
        var result = string.Join('\n', lines.Select((l, i) => prefix(i) + l));
        Replace(lineStart, lineEnd, result);
    }

    /// <summary>A suggested-change block holding the commented lines, on lines of its own, ready to edit.</summary>
    private void Suggest()
    {
        if (_vm?.SuggestionLines is not { } lines) return;
        var (start, end, text) = Selection();
        var before = start > 0 && text[start - 1] != '\n' ? "\n" : "";
        var body = string.Join('\n', lines);
        var block = before + "```suggestion\n" + body + "\n```\n";
        // The suggested lines are selected for editing.
        var bodyStart = before.Length + "```suggestion\n".Length;
        Replace(start, end, block, bodyStart, bodyStart + body.Length);
    }

    // ------------------------------------------------------------------ @mentions

    [GeneratedRegex(@"(?:^|[\s(\[])@(?<prefix>[A-Za-z0-9-]*)$")]
    private static partial Regex MentionBeforeCaret();

    private async Task UpdateMentionsAsync()
    {
        if (_vm is null || !Box.IsFocused && !Box.IsKeyboardFocusWithin)
        {
            CloseMentions();
            return;
        }
        var text = Box.Text ?? "";
        var caret = Math.Clamp(Box.CaretIndex, 0, text.Length);
        var match = MentionBeforeCaret().Match(text[..caret]);
        if (!match.Success)
        {
            CloseMentions();
            return;
        }
        var prefix = match.Groups["prefix"].Value;
        var request = ++_mentionRequest;
        var people = await _vm.MentionsAsync(prefix);
        if (request != _mentionRequest) return;
        if (people.Count == 0)
        {
            CloseMentions();
            return;
        }
        _mentionStart = match.Groups["prefix"].Index - 1;
        Mentions.ItemsSource = people.Select(p => new PersonViewModel(p, _vm.Avatars)).ToList();
        Mentions.SelectedIndex = 0;
        MentionPopup.IsOpen = true;
    }

    private void PickMention()
    {
        if (Mentions.SelectedItem is not PersonViewModel person || _mentionStart < 0)
        {
            CloseMentions();
            return;
        }
        var text = Box.Text ?? "";
        var caret = Math.Clamp(Box.CaretIndex, _mentionStart, text.Length);
        var start = _mentionStart;
        CloseMentions();
        Replace(start, caret, "@" + person.Login + " ");
    }

    private void CloseMentions()
    {
        _mentionStart = -1;
        _mentionRequest++;
        MentionPopup.IsOpen = false;
    }
}
