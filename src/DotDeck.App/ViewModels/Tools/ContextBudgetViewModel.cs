using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.AiCli.ContextBudget;
using DotDeck.Core.AiCli.Launch;

namespace DotDeck.App.ViewModels.Tools;

/// <summary>What the "컨텍스트" tab uses (supplied by the shell; tests pass their own).</summary>
/// <param name="LoadSettings">Current thresholds (read for every analysis, so settings apply right away).</param>
/// <param name="OpenFile">Opens an instruction, skill or configuration file in the editor.</param>
/// <param name="Confirm">Asks before an MCP server is started (title, message).</param>
public sealed record ContextBudgetServices(
    ContextCollector Collector,
    SessionLogReader Logs,
    McpMeasurementStore Measurements,
    McpProbe Probe,
    Func<ContextBudgetSettings> LoadSettings,
    Func<string, Task> OpenFile,
    Func<string, string, bool> Confirm);

/// <summary>
/// AI 모니터 "컨텍스트" tab: what the selected session's CLI reads before the first prompt. The measured baseline comes
/// from the session's own log (first request input minus the first prompt); inside it, DotDeck's estimate of the
/// instruction files and skill/agent descriptions, and the rest as "시스템·MCP 등 기타". Read-only: suggestions only.
/// Each running session is analyzed once when it appears, again when its files change (watched while selected), and
/// its log is re-read every <see cref="LogRefreshInterval"/> while it is selected.
/// </summary>
public sealed partial class ContextBudgetViewModel : ObservableObject, IDisposable
{
    public const string ConfirmTitle = "MCP 직접 측정";
    public const string ConfirmMessage = "MCP 서버를 실제로 실행해 도구 목록만 받고 종료합니다.";

    public static readonly TimeSpan LogRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly ContextBudgetServices _services;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ui;
    private readonly Dictionary<AiMonitorItemViewModel, Analysis> _analyses = [];
    private readonly HashSet<AiMonitorItemViewModel> _pending = [];
    private readonly Dictionary<string, (string ConfigHash, McpProbeResult Result)> _failures = new(StringComparer.Ordinal);
    private AiMonitorItemViewModel? _target;
    private ContextWatcher? _watcher;
    private DateTimeOffset _lastRefresh;

    private sealed record Analysis(ContextCollection Collection, ContextReport Report);

    public ContextBudgetViewModel(ContextBudgetServices services, TimeProvider? time = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _time = time ?? TimeProvider.System;
        _ui = SynchronizationContext.Current;
    }

    public ObservableCollection<ContextRowViewModel> Rows { get; } = [];

    public ObservableCollection<ContextSuggestionViewModel> Suggestions { get; } = [];

    public bool HasTarget => _target is not null;

    public bool HasReport => Report is not null;

    public bool HasSuggestions => Suggestions.Count > 0;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The report shown (the selected session's), or null.</summary>
    public ContextReport? Report { get; private set; }

    /// <summary>"고급 → MCP 직접 측정" is on: [측정] buttons are shown.</summary>
    public bool CanMeasureMcp { get; private set; }

    /// <summary>"기본 소모 약 1.8만 토큰 (실측)" or "(추정)" without a log measurement.</summary>
    public string SummaryText => Report is not { } report
        ? string.Empty
        : report.Log is { State: SessionLogState.Measured }
            ? $"기본 소모 약 {TokenEstimator.Format(report.Baseline)} 토큰 (실측)"
            : $"기본 소모 약 {TokenEstimator.Format(report.Total)} 토큰 (파일 추정)";

    /// <summary>"DotDeck이 잰 항목 2,307 (추정) · 시스템·MCP 등 기타 4.2만", or the log state.</summary>
    public string BreakdownText
    {
        get
        {
            if (Report is not { } report)
            {
                return string.Empty;
            }

            var files = $"지침·스킬 {TokenEstimator.Format(report.Total)} (추정)";
            return report.Log?.State switch
            {
                SessionLogState.Measured when report.Other is { } other => other >= 0
                    ? $"{files} · 시스템·MCP 등 기타 {TokenEstimator.Format(other)}"
                    : $"{files} · 파일 추정이 실측보다 큼 (추정 오차)",
                SessionLogState.WaitingFirstAnswer => files + " · 실측은 첫 응답 후 측정",
                SessionLogState.NotMatched => files + " · 세션 로그 매칭 불가",
                SessionLogState.Unreadable => files + " · 세션 로그 측정 불가",
                SessionLogState.Unsupported => files + " · agy는 파일 분석만 지원",
                _ => files,
            };
        }
    }

    /// <summary>
    /// "현재 컨텍스트 38% · Codex 로그 실측 · …" from the row's tracked value (the list's "컨텍스트" value), else
    /// "현재 입력 5.9만 / 20만 · 29.7% (추정)" from the log; empty without either.
    /// </summary>
    public string UsageText
    {
        get
        {
            if (_target?.ContextUsage is { State: SessionLogState.Measured } usage)
            {
                return "현재 컨텍스트 " + usage.Text + " · " + usage.ToolTip.Replace("\n", " · ", StringComparison.Ordinal);
            }

            if (Report?.Log is not { State: SessionLogState.Measured, CurrentInput: { } current } log)
            {
                return string.Empty;
            }

            if (log.ContextWindow is { } window && log.UsedPercent is { } percent)
            {
                var estimated = log.WindowIsEstimated ? " (추정)" : string.Empty;
                return string.Create(CultureInfo.InvariantCulture,
                    $"현재 입력 {TokenEstimator.Format(current)} / {TokenEstimator.Format(window)} · {percent:0.0}%{estimated}");
            }

            return $"현재 입력 {TokenEstimator.Format(current)} 토큰 (창 크기 모름)";
        }
    }

    /// <summary>Log details for the tooltip: file, first request, first prompt estimate.</summary>
    public string LogDetailText => Report?.Log is { State: SessionLogState.Measured } log
        ? string.Create(CultureInfo.InvariantCulture,
            $"세션 로그: {log.LogPath}\n첫 요청 입력 {log.FirstRequestInput:N0} − 첫 사용자 메시지(추정) {log.FirstPromptTokens:N0} = {log.Baseline:N0}")
        : "로그 형식은 비공식이라 바뀌면 측정 불가로 표시됩니다.";

    public string AnalyzedText => Report is { } report
        ? report.AnalyzedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " 분석"
        : string.Empty;

    public string ProblemsText => Report is { Problems.Count: > 0 } report ? string.Join("\n", report.Problems) : string.Empty;

    public bool HasProblems => ProblemsText.Length > 0;

    /// <summary>Shows the report of <paramref name="row"/> (analyzing it when it has none) and watches its files.</summary>
    public void Show(AiMonitorItemViewModel? row)
    {
        if (row == _target)
        {
            return;
        }

        if (_target is not null)
        {
            _target.PropertyChanged -= OnTargetChanged;
        }

        _target = row;
        if (row is not null)
        {
            row.PropertyChanged += OnTargetChanged;
        }

        _watcher?.Dispose();
        _watcher = null;
        OnPropertyChanged(nameof(HasTarget));
        if (row is not null && _analyses.TryGetValue(row, out var known))
        {
            Present(known);
            Watch(known.Collection);
            Analyze(row); // the log may have moved on
        }
        else
        {
            Present(null);
            if (row is not null && _pending.Contains(row))
            {
                IsBusy = true; // the start-up analysis is still running and will be shown
            }
            else if (row is not null)
            {
                Analyze(row);
            }
        }
    }

    private void OnTargetChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiMonitorItemViewModel.ContextUsage))
        {
            OnPropertyChanged(nameof(UsageText));
        }
    }

    /// <summary>Analyzes running rows that have never been analyzed ("세션 시작 시 1회").</summary>
    public void Track(IEnumerable<AiMonitorItemViewModel> rows)
    {
        foreach (var row in rows)
        {
            if (row.IsRunning && !_analyses.ContainsKey(row) && !_pending.Contains(row))
            {
                Analyze(row);
            }
        }
    }

    /// <summary>Forgets cleared rows.</summary>
    public void Forget(AiMonitorItemViewModel row)
    {
        _analyses.Remove(row);
        if (row == _target)
        {
            Show(null);
        }
    }

    /// <summary>Re-reads the selected running session's log now and then (UI thread; the monitor's timer).</summary>
    public void Tick()
    {
        if (_target is { IsRunning: true } row && _time.GetUtcNow() - _lastRefresh >= LogRefreshInterval && !_pending.Contains(row))
        {
            Analyze(row);
        }
    }

    [RelayCommand]
    private void Reanalyze()
    {
        if (_target is { } row)
        {
            Analyze(row);
        }
    }

    [RelayCommand]
    private Task OpenAsync(string? path) => string.IsNullOrEmpty(path) ? Task.CompletedTask : _services.OpenFile(path);

    [RelayCommand]
    private Task MeasureAsync(ContextRowViewModel? row) =>
        row?.ServerKey is { } key ? MeasureServersAsync([key]) : Task.CompletedTask;

    [RelayCommand]
    private Task MeasureAllAsync() =>
        MeasureServersAsync([.. Rows.Where(r => r.CanMeasure).Select(r => r.ServerKey!)]);

    private async Task MeasureServersAsync(IReadOnlyList<string> keys)
    {
        if (_target is not { } row || !_analyses.TryGetValue(row, out var analysis) || !CanMeasureMcp || keys.Count == 0)
        {
            return;
        }

        var servers = analysis.Collection.McpServers.Where(s => keys.Contains(s.Key)).DistinctBy(s => s.Key).ToList();

        // A stored measurement is the approval for that configuration; anything else asks once for the whole batch.
        var unapproved = servers.Where(s => _services.Measurements.Find(s) is null).ToList();
        if (unapproved.Count > 0
            && !_services.Confirm(ConfirmTitle, ConfirmMessage + "\n\n" + string.Join("\n", unapproved.Select(s => "· " + s))))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var folder = WorkingDirectory(row);
            foreach (var server in servers)
            {
                var result = await Task.Run(() => _services.Probe.MeasureAsync(server, folder)).ConfigureAwait(true);
                if (result.State == ContextItemState.Measured)
                {
                    _failures.Remove(server.Key);
                    _services.Measurements.Save(new McpMeasurement(server.Key, server.Name, result.ToolCount, result.Tokens, _time.GetUtcNow(), server.ConfigHash));
                }
                else
                {
                    _failures[server.Key] = (server.ConfigHash, result);
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        Analyze(row);
    }

    private void Analyze(AiMonitorItemViewModel row)
    {
        _pending.Add(row);
        if (row == _target)
        {
            IsBusy = true;
            _lastRefresh = _time.GetUtcNow();
        }

        var query = new SessionLogQuery(row.Cli, WorkingDirectory(row), row.StartedAt, row.Session?.ConversationId);
        var failures = new Dictionary<string, (string, McpProbeResult)>(_failures, StringComparer.Ordinal);
        var now = _time.GetUtcNow();
        _ = Task.Run(() =>
        {
            var settings = _services.LoadSettings().Normalize();
            var collection = _services.Collector.Collect(query.Cli, query.WorkingDirectory);
            var log = _services.Logs.Read(query, settings.EffectiveClaudeContextWindows);
            var report = ContextAnalyzer.Build(query.Cli, query.WorkingDirectory, collection, _services.Measurements, failures, settings, now, log);
            return (new Analysis(collection, report), settings.McpDirectMeasure);
        }).ContinueWith(task => Post(() =>
        {
            _pending.Remove(row);
            if (task.IsCompletedSuccessfully)
            {
                var (analysis, canMeasure) = task.Result;
                _analyses[row] = analysis;
                if (row == _target)
                {
                    var firstTime = Report is null || _watcher is null;
                    CanMeasureMcp = canMeasure;
                    Present(analysis);
                    if (firstTime)
                    {
                        Watch(analysis.Collection);
                    }
                }
            }

            if (row == _target)
            {
                IsBusy = false;
            }
        }), TaskScheduler.Default);
    }

    internal static string? WorkingDirectory(AiMonitorItemViewModel row) =>
        row.Session is { } session ? session.Scope.Primary.Root : row.TerminalPane?.Connection.WorkingDirectory;

    private void Watch(ContextCollection collection)
    {
        _watcher?.Dispose();
        var row = _target;
        _watcher = new ContextWatcher(collection.WatchDirectories, () => Post(() =>
        {
            if (row is not null && row == _target)
            {
                Analyze(row);
            }
        }));
    }

    private void Present(Analysis? analysis)
    {
        // Rows first, then the report: whoever sees the report also sees its rows.
        Rows.Clear();
        Suggestions.Clear();
        if (analysis?.Report is { } report)
        {
            var max = Math.Max(1, report.Items.Max(i => (int?)i.Tokens) ?? 1);
            foreach (var item in report.Items)
            {
                Rows.Add(new ContextRowViewModel(item, max, CanMeasureMcp));
            }

            foreach (var suggestion in report.Suggestions)
            {
                Suggestions.Add(new ContextSuggestionViewModel(suggestion.Text, suggestion.Path));
            }
        }

        Report = analysis?.Report;
        OnPropertyChanged(string.Empty);
        MeasureAllCommand.NotifyCanExecuteChanged();
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}

/// <summary>One bar of the "컨텍스트" tab.</summary>
public sealed class ContextRowViewModel(ContextItem item, int max, bool directMeasure)
{
    public const double BarMaxWidth = 90;

    public ContextItem Item { get; } = item;

    public string CategoryText => Item.Category switch
    {
        ContextCategory.Instructions => "지침",
        ContextCategory.Skills => "스킬",
        ContextCategory.Agents => "에이전트",
        _ => "MCP",
    };

    public string Name => Item.Name;

    public string Detail => Item.Detail ?? string.Empty;

    public string? Path => Item.Path;

    public string? ServerKey => Item.ServerKey;

    public bool Counts => Item.Counts;

    public string TokensText => Item.State switch
    {
        ContextItemState.Measured => Item.Tokens.ToString("N0", CultureInfo.InvariantCulture),
        ContextItemState.Disabled => "꺼짐",
        ContextItemState.AuthRequired => "인증 필요",
        ContextItemState.Failed => "측정 실패",
        _ => "미측정",
    };

    public double BarWidth => Item.Counts ? Math.Max(1, BarMaxWidth * Item.Tokens / max) : 0;

    /// <summary>"[측정]" is shown: MCP direct measuring is on and the server can be measured.</summary>
    public bool CanMeasure => directMeasure && Item.Category == ContextCategory.Mcp && Item.ServerKey is not null
        && Item.State is ContextItemState.NotMeasured or ContextItemState.Failed or ContextItemState.Measured
        && !(Item.Detail ?? string.Empty).Contains("측정하지 않음", StringComparison.Ordinal);

    public string ToolTip => (Path ?? Name) + (Detail.Length > 0 ? "\n" + Detail : string.Empty) + "\n(토큰은 추정값)";
}

public sealed record ContextSuggestionViewModel(string Text, string? Path)
{
    public bool HasPath => !string.IsNullOrEmpty(Path);
}
