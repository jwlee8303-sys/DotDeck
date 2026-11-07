using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Documents;
using DotDeck.Core.Files;

namespace DotDeck.App.ViewModels.Tools;

/// <summary>Global search over all folder projects (Phase 4). Runs off the UI thread and can be cancelled.</summary>
public sealed partial class SearchResultsPaneViewModel(IFileStore store, Func<IReadOnlyList<SearchRoot>> roots, Func<SearchHit, Task> open)
    : ToolPaneViewModel("tool.search", "검색 결과", ToolLocation.Bottom)
{
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private bool _useRegex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isSearching;

    [ObservableProperty]
    private string _summary = "검색어를 입력하고 Enter를 누르세요. 비밀·바이너리·2MB 초과 파일과 제외 폴더는 검색하지 않습니다.";

    [ObservableProperty]
    private SearchHit? _selected;

    public bool IsIdle => !IsSearching;

    public ObservableCollection<SearchHit> Results { get; } = [];

    /// <summary>Raised to move keyboard focus to the query box (Ctrl+Shift+F).</summary>
    public event EventHandler? FocusRequested;

    public void RequestFocus() => FocusRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task SearchAsync()
    {
        Stop();
        Results.Clear();
        var regex = TextSearch.TryBuild(new SearchOptions(Query, MatchCase, false, UseRegex), out var error);
        if (regex is null)
        {
            Summary = error ?? "검색어를 입력하세요.";
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        var targets = roots();
        IsSearching = true;
        Summary = "검색 중…";
        try
        {
            var hits = await Task.Run(() => GlobalSearch.Run(store, targets, regex, cts.Token).ToList(), cts.Token);
            foreach (var hit in hits)
            {
                Results.Add(hit);
            }

            var files = hits.Select(h => h.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Summary = hits.Count >= GlobalSearch.MaxHits
                ? $"결과가 많아 처음 {hits.Count}개만 표시합니다({files}개 파일)."
                : $"{hits.Count}개 결과, {files}개 파일";
        }
        catch (OperationCanceledException)
        {
            Summary = "검색을 취소했습니다.";
        }
        finally
        {
            if (_cts == cts)
            {
                IsSearching = false;
                _cts = null;
            }

            cts.Dispose();
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private Task OpenAsync(SearchHit? hit) => hit is null ? Task.CompletedTask : open(hit);
}
