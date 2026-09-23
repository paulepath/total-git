using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TotalGit.App.Views;

/// <summary>Simple modal confirmation with an optional list of details (paths, changed files…).</summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, IReadOnlyList<string>? details, string confirmText)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#23272D"));

        var confirm = new Button { Content = confirmText, IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        confirm.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);

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
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, confirm },
        });
        Content = panel;
    }
}
