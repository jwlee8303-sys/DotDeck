using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotDeck.App.ViewModels.Editor;

public sealed partial class KeyValueRow : FormRow
{
    [ObservableProperty]
    private string _key = string.Empty;
    public bool ForceSecret { get; set; }
    public bool IsSecret => ForceSecret || DotDeck.Core.Security.SecretMasker.IsSecretName(Key);
    partial void OnKeyChanged(string value) => OnPropertyChanged(nameof(IsSecret));
    protected override bool IsDisplayOnly(string? propertyName) => propertyName == nameof(IsSecret);

    [ObservableProperty]
    private string _value = string.Empty;
}

/// <summary>An ordered key-value editor; its owner decides validation, masking and serialization.</summary>
public sealed partial class KeyValueTableViewModel : ObservableObject
{
    private bool _loading;

    public KeyValueTableViewModel() => Rows.CollectionChanged += (_, _) => NotifyChanged();

    public ObservableCollection<KeyValueRow> Rows { get; } = [];

    public Action? Changed { get; init; }

    public void Load(IEnumerable<KeyValuePair<string, string>> values)
    {
        _loading = true;
        try
        {
            foreach (var row in Rows)
            {
                row.Changed = null;
            }

            Rows.Clear();
            foreach (var pair in values)
            {
                Rows.Add(Wire(new KeyValueRow { Key = pair.Key, Value = pair.Value }));
            }
        }
        finally
        {
            _loading = false;
        }
    }

    public IReadOnlyList<KeyValuePair<string, string>> Values() => [.. Rows.Select(r => KeyValuePair.Create(r.Key, r.Value))];

    [RelayCommand]
    private void Add() => Rows.Add(Wire(new KeyValueRow()));

    [RelayCommand]
    private void Remove(KeyValueRow? row)
    {
        if (row is not null && Rows.Remove(row))
        {
            row.Changed = null;
        }
    }

    public void Move(KeyValueRow row, int delta)
    {
        var index = Rows.IndexOf(row);
        if (index >= 0 && index + delta >= 0 && index + delta < Rows.Count)
        {
            Rows.Move(index, index + delta);
        }
    }

    private KeyValueRow Wire(KeyValueRow row)
    {
        row.Changed = (_, _) => NotifyChanged();
        return row;
    }

    private void NotifyChanged()
    {
        if (!_loading)
        {
            Changed?.Invoke();
        }
    }
}
