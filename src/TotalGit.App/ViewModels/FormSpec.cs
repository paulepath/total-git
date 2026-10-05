namespace TotalGit.App.ViewModels;

public enum FormFieldKind
{
    Text,
    MultilineText,
    CheckBox,
    Choice,

    /// <summary>A drop-down list of <see cref="FormField.Options"/> (type to jump to one).</summary>
    Select,
}

/// <summary>One option of a <see cref="FormFieldKind.Choice"/> field; a warning option is described in red.</summary>
public sealed record FormChoice(string Label, string Description, bool IsWarning = false);

/// <summary>One input of a <see cref="FormSpec"/>; the dialog writes the user's input back into it.</summary>
public sealed class FormField(FormFieldKind kind, string label)
{
    public FormFieldKind Kind { get; } = kind;
    public string Label { get; } = label;
    public string Text { get; set; } = "";
    public bool IsChecked { get; set; }
    public string? Placeholder { get; init; }

    /// <summary>The options of a choice field (radio buttons); <see cref="SelectedIndex"/> is the picked one.</summary>
    public IReadOnlyList<FormChoice> Choices { get; init; } = [];
    public int SelectedIndex { get; set; }

    /// <summary>The entries of a drop-down field; <see cref="SelectedIndex"/> is the picked one.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>A checkbox that must be ticked for this field to be enabled (e.g. "Annotated" → message).</summary>
    public FormField? EnabledBy { get; init; }

    /// <summary>False puts the caret after the initial text instead of selecting it (for a prefix such as "feature/").</summary>
    public bool SelectText { get; init; } = true;

    /// <summary>The height of a multiline field (taller for a commit message).</summary>
    public double Height { get; init; } = 96;

    public static FormField TextBox(string label, string text = "", string? placeholder = null, bool selectText = true) =>
        new(FormFieldKind.Text, label) { Text = text, Placeholder = placeholder, SelectText = selectText };

    public static FormField CheckBox(string label, bool isChecked = false) =>
        new(FormFieldKind.CheckBox, label) { IsChecked = isChecked };

    public static FormField Choice(string label, IReadOnlyList<FormChoice> choices, int selectedIndex = 0) =>
        new(FormFieldKind.Choice, label) { Choices = choices, SelectedIndex = selectedIndex };

    public static FormField Select(string label, IReadOnlyList<string> options, int selectedIndex = 0) =>
        new(FormFieldKind.Select, label) { Options = options, SelectedIndex = selectedIndex };
}

/// <summary>A small modal form: title, explanation, some fields and a confirm button.</summary>
/// <param name="Validate">Returns an error to show (and disable confirm), or null when the input is acceptable.</param>
public sealed record FormSpec(
    string Title,
    string? Message,
    string ConfirmText,
    IReadOnlyList<FormField> Fields,
    Func<string?>? Validate = null);
