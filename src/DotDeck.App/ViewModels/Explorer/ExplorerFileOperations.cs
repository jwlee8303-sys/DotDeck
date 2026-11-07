using System.IO;
using DotDeck.App.Services;
using DotDeck.Core.FileOperations;
using DotDeck.Core.Logging;

namespace DotDeck.App.ViewModels.Explorer;

/// <summary>Raised on the UI thread after a file operation changed the disk.</summary>
/// <param name="InvolvedPaths">Paths whose folders may have changed (sources, targets, results).</param>
/// <param name="ResultPaths">Items to select after refreshing.</param>
/// <param name="RefreshAll">Undo/redo: the touched folders are not known here, so every explorer refreshes.</param>
public sealed record FileOperationCompleted(IReadOnlyList<string> InvolvedPaths, IReadOnlyList<string> ResultPaths, bool RefreshAll);

/// <summary>
/// UI side of <see cref="FileOperationService"/>: runs operations off the UI thread, reports failures,
/// and announces completion so explorers can refresh. Shared by all explorers (one global undo history, Q10).
/// </summary>
public sealed class ExplorerFileOperations
{
    private readonly FileOperationService _service;
    private readonly IDialogService _dialogs;
    private readonly IAppLog _log;
    private readonly Action<string> _setStatus;

    public ExplorerFileOperations(FileOperationService service, IDialogService dialogs, IAppLog log, Action<string> setStatus)
    {
        _service = service;
        _dialogs = dialogs;
        _log = log;
        _setStatus = setStatus;
    }

    public event EventHandler<FileOperationCompleted>? Completed;

    public FileOperationHistory History => _service.History;

    public FileOperationService Service => _service;

    /// <summary>
    /// Runs <paramref name="operation"/> on the thread pool. Returns the result, or null when it failed
    /// (the user has been told why). Must be called on the UI thread; continues there.
    /// </summary>
    public async Task<FileOperationResult?> RunAsync(
        Func<FileOperationService, FileOperationResult?> operation,
        IReadOnlyList<string> involvedPaths,
        bool refreshAll = false)
    {
        try
        {
            var result = await Task.Run(() => operation(_service));
            if (result is null)
            {
                return null;
            }

            _log.Info($"파일 작업: {result.Description}");
            _setStatus(result.Description);
            foreach (var warning in result.Warnings)
            {
                _log.Warning(warning);
                _dialogs.ShowError("파일 작업", warning);
            }

            Completed?.Invoke(this, new FileOperationCompleted([.. involvedPaths, .. result.Paths], result.Paths, refreshAll));
            return result;
        }
        catch (FileOperationException ex)
        {
            _log.Warning($"파일 작업을 하지 못했습니다: {ex.Message}", ex.InnerException);
            _setStatus(FirstLine(ex.Message));
            _dialogs.ShowError("파일 작업", ex.Message);

            // A partial result changed the disk: show it.
            if (ex.PartiallyCompleted || refreshAll)
            {
                Completed?.Invoke(this, new FileOperationCompleted(involvedPaths, [], refreshAll));
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("파일 작업 중 오류가 발생했습니다.", ex);
            _dialogs.ShowError("파일 작업", ex.Message);
            Completed?.Invoke(this, new FileOperationCompleted(involvedPaths, [], refreshAll));
            return null;
        }
    }

    public Task<FileOperationResult?> UndoAsync() => RunAsync(s => s.Undo(), [], refreshAll: true);

    public Task<FileOperationResult?> RedoAsync() => RunAsync(s => s.Redo(), [], refreshAll: true);

    private static string FirstLine(string text) => text.Split('\n')[0];
}
