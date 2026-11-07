using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Logging;

namespace DotDeck.App.ViewModels.Tools;

/// <summary>Shows application log entries. Entries may arrive on any thread and are marshalled to the UI.</summary>
public sealed partial class OutputPaneViewModel : ToolPaneViewModel
{
    public const int MaxEntries = 2000;

    private readonly SynchronizationContext? _uiContext;

    public OutputPaneViewModel(IAppLog log)
        : base("tool.output", "출력", ToolLocation.Bottom)
    {
        _uiContext = SynchronizationContext.Current;
        log.EntryWritten += OnEntryWritten;
    }

    public ObservableCollection<string> Lines { get; } = [];

    [RelayCommand]
    private void Clear() => Lines.Clear();

    private void OnEntryWritten(object? sender, AppLogEntry entry)
    {
        // Entry text is already masked by the log. Show only the first exception line (type: message).
        var detail = entry.Exception is null ? string.Empty : " — " + FirstLine(entry.Exception);
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.Timestamp:HH:mm:ss} [{entry.Level}] {entry.Message}{detail}");

        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            Append(line);
        }
        else
        {
            _uiContext.Post(_ => Append(line), null);
        }
    }

    private static string FirstLine(string text)
    {
        var end = text.AsSpan().IndexOfAny('\r', '\n');
        return end < 0 ? text : text[..end];
    }

    private void Append(string line)
    {
        Lines.Add(line);
        while (Lines.Count > MaxEntries)
        {
            Lines.RemoveAt(0);
        }
    }
}
