using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TotalGit.App.Services;
using TotalGit.Core.Git;

namespace TotalGit.App.ViewModels;

/// <summary>One rule being edited, with a preview of its icon and which of the repository's branches it matches.</summary>
public sealed partial class BranchRuleItem : ObservableObject
{
    private readonly BranchRulesViewModel _owner;

    public BranchRuleItem(BranchRulesViewModel owner, BranchRule rule)
    {
        _owner = owner;
        Pattern = rule.Pattern;
        GroupUnder = rule.GroupUnder ?? "";
        HidePrefix = rule.HidePrefix;
        Icon = rule.Icon;
        IconColor = rule.IconColor;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string Pattern { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string GroupUnder { get; set; }

    [ObservableProperty]
    public partial bool HidePrefix { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconBitmap), nameof(HasIcon), nameof(CanRecolour))]
    public partial string? Icon { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconBitmap), nameof(ColourText))]
    public partial string? IconColor { get; set; }

    partial void OnPatternChanged(string value) => _owner.RuleChanged(this);
    partial void OnGroupUnderChanged(string value) => _owner.RuleChanged(this);
    partial void OnHidePrefixChanged(bool value) => _owner.RuleChanged(this);
    partial void OnIconChanged(string? value) => _owner.RuleChanged(this);
    partial void OnIconColorChanged(string? value) => _owner.ColourChanged(this);

    public Bitmap? IconBitmap => Icon is { } id ? IconLibrary.Get(id, IconColor) : null;
    public bool HasIcon => IconBitmap is not null;

    /// <summary>Only the line icons take a colour; pictures keep their own.</summary>
    public bool CanRecolour => Icon?.StartsWith("lucide:", StringComparison.Ordinal) == true;

    /// <summary>The custom colour box: empty for the icon's default colour.</summary>
    public string ColourText
    {
        get => IconColor ?? "";
        set => IconColor = Color.TryParse(value, out _) ? value : string.IsNullOrWhiteSpace(value) ? null : IconColor;
    }

    public string Summary => string.IsNullOrWhiteSpace(GroupUnder) ? Pattern : $"{Pattern}  →  {GroupUnder}";

    public BranchRule ToRule() => new()
    {
        Pattern = Pattern.Trim(),
        GroupUnder = string.IsNullOrWhiteSpace(GroupUnder) ? null : GroupUnder.Trim().Trim('/'),
        HidePrefix = HidePrefix,
        Icon = Icon,
        IconColor = IconColor,
    };
}

/// <summary>
/// An icon in the picker, drawn in the selected rule's colour. A class, not a record: the picker list is rebuilt
/// for each rule, and equal-looking items would carry the old rule's selection over to the new one.
/// </summary>
public sealed class IconChoice(IconInfo info, Bitmap? preview)
{
    public IconInfo Info { get; } = info;
    public Bitmap? Preview { get; } = preview;
    public string Id => Info.Id;
    public string ToolTip => Info.Name;
}

/// <summary>A colour swatch in the rule editor (null: the icon's default colour).</summary>
public sealed record ColourChoice(string? Hex, IBrush Brush, string ToolTip);

/// <summary>
/// The branch rules editor: the ordered rules on the left; the selected rule's pattern, group, icon and colour on
/// the right, with which of the repository's branches it would match.
/// </summary>
public sealed partial class BranchRulesViewModel : ObservableObject
{
    private readonly IReadOnlyList<string> _branchNames;
    private readonly Func<string, string, Task<bool>> _confirm;

    public BranchRulesViewModel(IReadOnlyList<BranchRule> rules, IReadOnlyList<string> branchNames, Func<string, string, Task<bool>> confirm,
        string? newPattern = null)
    {
        _branchNames = branchNames;
        _confirm = confirm;
        foreach (var r in rules) Rules.Add(new BranchRuleItem(this, r));
        Colours =
        [
            new(null, new SolidColorBrush(Color.Parse(IconLibrary.DefaultColour)), "Default"),
            .. new[] { "#E5484D", "#F76B15", "#E8B339", "#4CC38A", "#12A594", "#5AA9F2", "#2D7BF4", "#A371F7", "#E879F9", "#8A9099" }
                .Select(h => new ColourChoice(h, new SolidColorBrush(Color.Parse(h)), h)),
        ];
        if (newPattern is not null)
        {
            // Opened from a branch or folder: start a rule for it (at the top, so it wins over the defaults).
            var item = new BranchRuleItem(this, new BranchRule { Pattern = newPattern, Icon = "lucide:git-branch" });
            Rules.Insert(0, item);
            Selected = item;
        }
        else
        {
            Selected = Rules.FirstOrDefault();
        }
        RefreshIcons();
    }

    public ObservableCollection<BranchRuleItem> Rules { get; } = [];
    public IReadOnlyList<ColourChoice> Colours { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(MatchText))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(DuplicateCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    public partial BranchRuleItem? Selected { get; set; }

    partial void OnSelectedChanged(BranchRuleItem? value) => RefreshIcons();

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    public partial IReadOnlyList<IconChoice> Icons { get; private set; } = [];

    [ObservableProperty]
    public partial string IconFilter { get; set; } = "";

    partial void OnIconFilterChanged(string value) => RefreshIcons();

    [ObservableProperty]
    public partial string? UploadError { get; set; }

    /// <summary>The selected icon in the picker grid (setting it gives the rule that icon).</summary>
    public IconChoice? SelectedIcon
    {
        get => Icons.FirstOrDefault(i => i.Id == Selected?.Icon);
        set
        {
            // While the list is rebuilt the picker reports its old selection; only a user's pick counts.
            if (_refreshingIcons || Selected is null || value is null || value.Id == Selected.Icon) return;
            Selected.Icon = value.Id;
        }
    }

    /// <summary>Which branches the selected rule applies to (rules above it take the ones they match first).</summary>
    public string MatchText
    {
        get
        {
            if (Selected is null) return "";
            if (string.IsNullOrWhiteSpace(Selected.Pattern)) return "Type a pattern, e.g. bug/* or release-*";
            var set = new BranchRuleSet(Rules.Select(r => r.ToRule()));
            var rule = Selected.ToRule();
            var matches = _branchNames.Where(n => set.Match(n).Rule is { } m && SameRule(m, rule)).ToList();
            if (matches.Count == 0) return "Matches none of this repository's branches.";
            var shown = string.Join(", ", matches.Take(6));
            return $"Matches {matches.Count} branch{(matches.Count == 1 ? "" : "es")}: {shown}{(matches.Count > 6 ? ", …" : "")}";
        }
    }

    private static bool SameRule(BranchRule a, BranchRule b) =>
        string.Equals(a.Pattern, b.Pattern, StringComparison.OrdinalIgnoreCase);

    internal void RuleChanged(BranchRuleItem item)
    {
        if (item == Selected)
        {
            OnPropertyChanged(nameof(MatchText));
            OnPropertyChanged(nameof(SelectedIcon));
        }
    }

    internal void ColourChanged(BranchRuleItem item)
    {
        if (item == Selected) RefreshIcons();
    }

    /// <summary>The icons matching the search, drawn in the selected rule's colour.</summary>
    private void RefreshIcons()
    {
        var colour = Selected?.IconColor;
        var filter = IconFilter.Trim();
        _refreshingIcons = true;
        try
        {
            Icons = IconLibrary.All()
                .Where(i => filter.Length == 0 || i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Select(i => new IconChoice(i, IconLibrary.Get(i.Id, i.CanRecolour ? colour : null)))
                .ToList();
            OnPropertyChanged(nameof(SelectedIcon));
        }
        finally
        {
            _refreshingIcons = false;
        }
    }

    private bool _refreshingIcons;

    [RelayCommand]
    private void PickColour(ColourChoice choice)
    {
        if (Selected is null) return;
        Selected.IconColor = choice.Hex;
        RefreshIcons();
    }

    [RelayCommand]
    private void ClearIcon()
    {
        if (Selected is not null) Selected.Icon = null;
    }

    /// <summary>Adds a picture the user picked to their icons and gives it to the selected rule.</summary>
    public void AddCustomIcon(string path)
    {
        var (id, error) = IconLibrary.AddCustom(path);
        UploadError = error;
        if (id is null) return;
        RefreshIcons();
        if (Selected is not null) Selected.Icon = id;
    }

    [RelayCommand]
    private void Add()
    {
        var item = new BranchRuleItem(this, new BranchRule { Pattern = "", Icon = "lucide:git-branch" });
        Rules.Insert(Selected is null ? 0 : Rules.IndexOf(Selected) + 1, item);
        Selected = item;
    }

    private bool HasSelected() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Duplicate()
    {
        if (Selected is null) return;
        var item = new BranchRuleItem(this, Selected.ToRule());
        Rules.Insert(Rules.IndexOf(Selected) + 1, item);
        Selected = item;
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Delete()
    {
        if (Selected is null) return;
        var index = Rules.IndexOf(Selected);
        Rules.Remove(Selected);
        Selected = Rules.Count == 0 ? null : Rules[Math.Min(index, Rules.Count - 1)];
    }

    private bool CanMoveUp() => Selected is not null && Rules.IndexOf(Selected) > 0;
    private bool CanMoveDown() => Selected is not null && Rules.IndexOf(Selected) < Rules.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private void Move(int by)
    {
        if (Selected is not { } item) return;
        var index = Rules.IndexOf(item);
        Rules.Move(index, index + by);
        Selected = item;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(MatchText));
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (!await _confirm("Reset branch rules?", "Replace your rules with the built-in ones (feature/, bug/, hot-fix/, main…)?")) return;
        Rules.Clear();
        foreach (var r in BranchRuleSet.Defaults()) Rules.Add(new BranchRuleItem(this, r));
        Selected = Rules.FirstOrDefault();
    }

    /// <summary>The rules to save (blank patterns dropped).</summary>
    public List<BranchRule> Result() => Rules.Select(r => r.ToRule()).Where(r => r.Pattern.Length > 0).ToList();
}
