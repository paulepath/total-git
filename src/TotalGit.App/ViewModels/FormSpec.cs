namespace TotalGit.App.ViewModels;

public enum FormFieldKind
{
    Text,
    MultilineText,
    CheckBox,
}

/// <summary>One input of a <see cref="FormSpec"/>; the dialog writes the user's input back into it.</summary>
public sealed class FormField(FormFieldKind kind, string label)
{
    public FormFieldKind Kind { get; } = kind;
    public string Label { get; } = label;
    public string Text { get; set; } = "";
    public bool IsChecked { get; set; }
    public string? Placeholder { get; init; }

    /// <summary>A checkbox that must be ticked for this field to be enabled (e.g. "Annotated" → message).</summary>
    public FormField? EnabledBy { get; init; }

    public static FormField TextBox(string label, string text = "", string? placeholder = null) =>
        new(FormFieldKind.Text, label) { Text = text, Placeholder = placeholder };

    public static FormField CheckBox(string label, bool isChecked = false) =>
        new(FormFieldKind.CheckBox, label) { IsChecked = isChecked };
}

/// <summary>A small modal form: title, explanation, some fields and a confirm button.</summary>
/// <param name="Validate">Returns an error to show (and disable confirm), or null when the input is acceptable.</param>
public sealed record FormSpec(
    string Title,
    string? Message,
    string ConfirmText,
    IReadOnlyList<FormField> Fields,
    Func<string?>? Validate = null);
