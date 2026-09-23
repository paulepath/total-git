using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

/// <summary>Modal dialog for a <see cref="FormSpec"/> (create tag, stash, merge options…).</summary>
public sealed class FormDialog : Window
{
    private readonly FormSpec _spec;
    private readonly Button _confirm;
    private readonly TextBlock _error;
    private readonly List<(FormField Field, Control Control)> _controls = [];

    public FormDialog(FormSpec spec)
    {
        _spec = spec;
        Title = spec.Title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#23272D"));

        _confirm = new Button { Content = spec.ConfirmText, IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        _confirm.Click += (_, _) =>
        {
            if (Validate()) Close(true);
        };
        cancel.Click += (_, _) => Close(false);
        _error = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#F26B6B")), TextWrapping = TextWrapping.Wrap, IsVisible = false };

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = spec.Title, FontSize = 18, FontWeight = FontWeight.SemiBold });
        if (spec.Message is { } message)
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#A9AFB8")) });

        foreach (var field in spec.Fields) panel.Children.Add(Build(field));

        panel.Children.Add(_error);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, _confirm },
        });
        Content = panel;

        Opened += (_, _) =>
        {
            if (_controls.Select(c => c.Control).OfType<TextBox>().FirstOrDefault() is { } first)
            {
                first.Focus();
                first.SelectAll();
            }
        };
        UpdateEnabled();
        Validate();
    }

    private Control Build(FormField field)
    {
        if (field.Kind == FormFieldKind.CheckBox)
        {
            var box = new CheckBox { Content = field.Label, IsChecked = field.IsChecked };
            box.IsCheckedChanged += (_, _) =>
            {
                field.IsChecked = box.IsChecked == true;
                UpdateEnabled();
                Validate();
            };
            _controls.Add((field, box));
            return box;
        }

        var multiline = field.Kind == FormFieldKind.MultilineText;
        var text = new TextBox
        {
            Text = field.Text,
            PlaceholderText = field.Placeholder,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Height = multiline ? 96 : double.NaN,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        text.TextChanged += (_, _) =>
        {
            field.Text = text.Text ?? "";
            Validate();
        };
        _controls.Add((field, text));
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = field.Label, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#8A9099")) },
                text,
            },
        };
    }

    private void UpdateEnabled()
    {
        foreach (var (field, control) in _controls)
            if (field.EnabledBy is { } by) control.IsEnabled = by.IsChecked;
    }

    private bool Validate()
    {
        var error = _spec.Validate?.Invoke();
        _error.Text = error;
        _error.IsVisible = error is not null;
        _confirm.IsEnabled = error is null;
        return error is null;
    }
}
