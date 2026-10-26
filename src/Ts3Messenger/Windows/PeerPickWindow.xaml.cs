using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Ts3Messenger.Windows;

public partial class PeerPickWindow : Window
{
    private readonly ObservableCollection<PickPerson> _people = new();
    private bool _selectAllGuard;

    public IReadOnlyList<string> SelectedIds { get; private set; } = [];

    public PeerPickWindow(IEnumerable<PickPerson> people, string title, string confirmText)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        ConfirmButton.Content = confirmText;
        foreach (var person in people)
        {
            person.PropertyChanged += OnPersonChanged;
            _people.Add(person);
        }

        PeopleList.ItemsSource = _people;
        CollectionViewSource.GetDefaultView(_people).Filter = MatchesSearch;
        UpdateCount();
    }

    private void OnPersonChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PickPerson.IsChecked) || _selectAllGuard)
            return;
        UpdateCount();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) =>
        CollectionViewSource.GetDefaultView(_people).Refresh();

    private bool MatchesSearch(object item)
    {
        var query = SearchBox?.Text.Trim() ?? "";
        return query.Length == 0
               || item is PickPerson person && person.LoginId.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSelectAllChecked(object sender, RoutedEventArgs e) => SetAll(true);

    private void OnSelectAllUnchecked(object sender, RoutedEventArgs e)
    {
        if (_selectAllGuard)
            return;
        SetAll(false);
    }

    private void SetAll(bool selected)
    {
        _selectAllGuard = true;
        try
        {
            foreach (var person in _people)
                person.IsChecked = selected;
        }
        finally
        {
            _selectAllGuard = false;
        }

        UpdateCount();
    }

    private void UpdateCount()
    {
        var selected = _people.Count(p => p.IsChecked);
        CountText.Text = $"{selected} / {_people.Count}";
        ConfirmButton.IsEnabled = selected > 0;
        _selectAllGuard = true;
        try
        {
            SelectAllBox.IsChecked = _people.Count > 0 && selected == _people.Count;
        }
        finally
        {
            _selectAllGuard = false;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        SelectedIds = _people.Where(p => p.IsChecked).Select(p => p.Id).ToList();
        if (SelectedIds.Count == 0)
            return;
        DialogResult = true;
        Close();
    }
}

public sealed class PickPerson : INotifyPropertyChanged
{
    private bool _isChecked;

    public required string Id { get; init; }
    public required string LoginId { get; init; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
