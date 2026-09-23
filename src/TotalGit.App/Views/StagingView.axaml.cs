using System.ComponentModel;
using Avalonia.Controls;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

public partial class StagingView : UserControl
{
    private bool _syncing;
    private StagingViewModel? _vm;

    public StagingView()
    {
        InitializeComponent();
        // One selection across both lists: picking a file in one clears the other. Only user
        // selections count; lists being repopulated by a status refresh must not clear it.
        UnstagedList.SelectionChanged += (_, e) => { if (e.AddedItems.Count > 0) OnSelected(UnstagedList, StagedList); };
        StagedList.SelectionChanged += (_, e) => { if (e.AddedItems.Count > 0) OnSelected(StagedList, UnstagedList); };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as StagingViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StagingViewModel.SelectedFile) || _syncing || _vm is null) return;
        _syncing = true;
        try
        {
            var selected = _vm.SelectedFile;
            UnstagedList.SelectedItem = selected is { IsStaged: false } ? selected : null;
            StagedList.SelectedItem = selected is { IsStaged: true } ? selected : null;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSelected(ListBox source, ListBox other)
    {
        if (_syncing || _vm is null || source.SelectedItem is not FileChangeItem item) return;
        _syncing = true;
        try
        {
            other.SelectedItem = null;
            _vm.SelectedFile = item;
        }
        finally
        {
            _syncing = false;
        }
    }
}
