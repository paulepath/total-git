using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

/// <summary>Simple modal confirmation with an optional list of details (paths, changed files…).</summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, IReadOnlyList<string>? details, string confirmText)
    {
        var confirm = new Button { Content = confirmText, IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        confirm.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);
        Build(this, title, message, details, [cancel, confirm]);
    }

    /// <summary>The shared layout: title, message, optional details box, then the buttons on the right.</summary>
    internal static void Build(Window window, string title, string message, IReadOnlyList<string>? details, IReadOnlyList<Button> buttons)
    {
        window.Title = title;
        window.Width = 540;
        window.SizeToContent = SizeToContent.Height;
        window.CanResize = false;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Background = new SolidColorBrush(Color.Parse("#23272D"));

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (details is { Count: > 0 })
        {
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1C1F24")),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Child = new ScrollViewer
                {
                    MaxHeight = 220,
                    Content = new SelectableTextBlock
                    {
                        Text = string.Join(Environment.NewLine, details),
                        FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#C2C7CE")),
                    },
                },
            });
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        foreach (var b in buttons) row.Children.Add(b);
        panel.Children.Add(row);
        window.Content = panel;
    }
}

/// <summary>A question with several answers (e.g. "Force push" or "Pull"); closes with the chosen index, or null for Cancel.</summary>
public sealed class ChoiceDialog : Window
{
    private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#B4373C"));

    public ChoiceDialog(string title, string message, IReadOnlyList<string>? details, IReadOnlyList<DialogChoice> choices)
    {
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        var buttons = new List<Button> { cancel };
        for (var i = 0; i < choices.Count; i++)
        {
            var index = i;
            var choice = choices[i];
            var button = new Button { Content = choice.Text, IsDefault = choice.IsPrimary };
            if (choice.IsPrimary) button.Classes.Add("accent");
            if (choice.IsDanger) button.Background = DangerBrush;
            if (choice.ToolTip is { } tip) ToolTip.SetTip(button, tip);
            button.Click += (_, _) => Close(index);
            buttons.Add(button);
        }
        ConfirmDialog.Build(this, title, message, details, buttons);
    }
}
