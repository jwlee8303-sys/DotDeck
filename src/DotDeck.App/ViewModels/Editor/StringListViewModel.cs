using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotDeck.App.ViewModels.Editor;

public sealed partial class StringListRow : FormRow
{
    [ObservableProperty]
    private string _value = string.Empty;
}

/// <summary>A string list editable as rows or delimited text. Loading never writes back.</summary>
public sealed partial class StringListViewModel(char separator = ',') : ObservableObject
{
    private bool _loading;
    private string _text = string.Empty;

    public ObservableCollection<StringListRow> Rows { get; } = [];

    public Action? Changed { get; init; }

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                Reset(value.Split(separator).Select(v => v.Trim()).Where(v => v.Length > 0));
                OnPropertyChanged(nameof(Values));
                Changed?.Invoke();
            }
        }
    }

    public IReadOnlyList<string> Values => [.. Rows.Select(r => r.Value.Trim()).Where(v => v.Length > 0)];

    public void Load(IEnumerable<string> values)
    {
        var snapshot = values.ToList();
        Reset(snapshot);
        SetProperty(ref _text, string.Join($"{separator} ", snapshot), nameof(Text));
        OnPropertyChanged(nameof(Values));
    }

    public void AddUnique(string value)
    {
        if (!Values.Contains(value, StringComparer.Ordinal))
        {
            Add(value);
        }
    }

    [RelayCommand]
    private void Add(string? value)
    {
        Rows.Add(Wire(new StringListRow { Value = value ?? string.Empty }));
        WriteText();
    }

    [RelayCommand]
    private void Remove(StringListRow? row)
    {
        if (row is not null && Rows.Remove(row))
        {
            row.Changed = null;
            WriteText();
        }
    }

    [RelayCommand]
    private void MoveUp(StringListRow? row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(StringListRow? row) => Move(row, 1);

    private void Move(StringListRow? row, int delta)
    {
        var index = row is null ? -1 : Rows.IndexOf(row);
        if (index >= 0 && index + delta >= 0 && index + delta < Rows.Count)
        {
            Rows.Move(index, index + delta);
            WriteText();
        }
    }

    private StringListRow Wire(StringListRow row)
    {
        row.Changed = (_, _) => WriteText();
        return row;
    }

    private void Reset(IEnumerable<string> values)
    {
        _loading = true;
        try
        {
            foreach (var row in Rows)
            {
                row.Changed = null;
            }

            Rows.Clear();
            foreach (var value in values)
            {
                Rows.Add(Wire(new StringListRow { Value = value }));
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void WriteText()
    {
        if (!_loading)
        {
            SetProperty(ref _text, string.Join($"{separator} ", Values), nameof(Text));
            OnPropertyChanged(nameof(Values));
            Changed?.Invoke();
        }
    }
}
