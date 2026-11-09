using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Settings;

namespace DotDeck.App.ViewModels.Dialogs;

public sealed partial class ExclusionListViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<string>> _pickFiles;
    private readonly Func<string?> _pickFolder;
    public ExclusionListViewModel(IEnumerable<ExcludedPath> items, Func<IReadOnlyList<string>> pickFiles, Func<string?> pickFolder)
    {
        Items = [.. items]; _pickFiles = pickFiles; _pickFolder = pickFolder;
    }
    public ObservableCollection<ExcludedPath> Items { get; }
    [ObservableProperty] private string _pathText = string.Empty;
    [ObservableProperty] private bool _isFolder;
    [ObservableProperty] private ExcludedPath? _selected;
    [ObservableProperty] private string? _error;
    [RelayCommand]
    private void Add()
    {
        try
        {
            var path = ExcludedPath.Normalize(PathText);
            if (!Items.Any(e => e.IsDirectory == IsFolder && e.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                Items.Add(new ExcludedPath(path, IsFolder));
            PathText = string.Empty; Error = null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { Error = ex.Message; }
    }
    [RelayCommand]
    private void BrowseFiles()
    {
        foreach (var path in _pickFiles()) { PathText = path; IsFolder = false; Add(); }
    }
    [RelayCommand]
    private void BrowseFolder()
    {
        if (_pickFolder() is { } path) { PathText = path; IsFolder = true; Add(); }
    }
    [RelayCommand] private void Remove() { if (Selected is { } selected) Items.Remove(selected); }
}

public sealed partial class ExclusionSettingsViewModel : ObservableObject
{
    private readonly PathExclusionStore _store;
    public ExclusionSettingsViewModel(PathExclusionStore store, Func<IReadOnlyList<string>> pickFiles, Func<string?> pickFolder)
    {
        _store = store;
        Backup = new(store.Get(ExclusionKind.AutomaticBackup), pickFiles, pickFolder);
        Review = new(store.Get(ExclusionKind.ChangeReview), pickFiles, pickFolder);
    }
    public ExclusionListViewModel Backup { get; }
    public ExclusionListViewModel Review { get; }
    [ObservableProperty] private string? _error;
    public event EventHandler? Saved;
    [RelayCommand]
    private async Task SaveAsync()
    {
        var backup = Backup.Items.ToArray(); var review = Review.Items.ToArray();
        try { await Task.Run(() => _store.Replace(backup, review)).ConfigureAwait(true); Saved?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Error = "제외항목을 저장하지 못했습니다: " + ex.Message; }
    }
}
