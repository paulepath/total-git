using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TotalGit.App.ViewModels;

/// <summary>A row of a clean-up dialog: something to delete, ticked or not.</summary>
public interface ICleanupItem : INotifyPropertyChanged
{
    /// <summary>False when it's listed but can't be deleted.</summary>
    bool IsSelectable { get; }
    bool IsChecked { get; set; }
}

/// <summary>A clean-up dialog's tick list: what's selected, its Select all box and its Delete button's text.</summary>
public abstract partial class CleanupListViewModel<T> : ObservableObject where T : class, ICleanupItem
{
    private readonly string _one;
    private readonly string _many;
    private bool _updatingAll;

    /// <param name="one">What one item is called on the Delete button ("branch").</param>
    /// <param name="many">The same for several ("branches").</param>
    protected CleanupListViewModel(IEnumerable<T> items, string one, string many)
    {
        _one = one;
        _many = many;
        Items = new ObservableCollection<T>(items);
        foreach (var item in Items) item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ICleanupItem.IsChecked)) OnSelectionChanged();
        };
        OnSelectionChanged();
    }

    public ObservableCollection<T> Items { get; }

    public IReadOnlyList<T> Selected => Items.Where(i => i.IsSelectable && i.IsChecked).ToList();

    private int SelectableCount => Items.Count(i => i.IsSelectable);

    public string SelectionText => $"{Selected.Count} of {SelectableCount} selected";

    public bool CanDelete => Selected.Count > 0;

    public string DeleteText => Selected.Count switch
    {
        0 => "Delete",
        1 => $"Delete 1 {_one}",
        var n => $"Delete {n} {_many}",
    };

    /// <summary>Select all: checks or unchecks every deletable item; shows "mixed" (null) for a partial selection.</summary>
    [ObservableProperty]
    public partial bool? AllChecked { get; set; }

    partial void OnAllCheckedChanged(bool? oldValue, bool? newValue)
    {
        if (_updatingAll) return;
        // Clicking a mixed box selects all (the checkbox itself would go to "none").
        var check = oldValue is null || newValue != false;
        _updatingAll = true;
        foreach (var item in Items.Where(i => i.IsSelectable)) item.IsChecked = check;
        _updatingAll = false;
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        if (_updatingAll) return;
        var selected = Selected.Count;
        _updatingAll = true;
        AllChecked = selected == 0 ? false : selected == SelectableCount ? true : null;
        _updatingAll = false;
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(DeleteText));
    }
}
