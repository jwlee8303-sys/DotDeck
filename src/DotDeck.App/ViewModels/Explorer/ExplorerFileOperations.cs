using System.IO;
using DotDeck.App.Services;
using DotDeck.Core.FileOperations;
using DotDeck.Core.Logging;

namespace DotDeck.App.ViewModels.Explorer;

/// <summary>Raised on the UI thread after a file operation changed the disk.</summary>
/// <param name="InvolvedPaths">Paths whose folders may have changed (sources, targets, results).</param>
/// <param name="ResultPaths">Items to select after refreshing.</param>
/// <param name="RefreshAll">Undo/redo: the touched folders are not known here, so every explorer refreshes.</param>
/// <param name="Changes">What moved, appeared or went away (visibility rules, pins and flat order follow them).</param>
public sealed record FileOperationCompleted(
    IReadOnlyList<string> InvolvedPaths,
    IReadOnlyList<string> ResultPaths,
    bool RefreshAll,
    IReadOnlyList<PathChange> Changes);

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

    /// <summary>
    /// Runs on the UI thread after an operation changed the disk (explorers update rules and refresh); awaited, so
    /// the caller of <see cref="RunAsync"/> sees the refreshed trees.
    /// </summary>
    public Func<FileOperationCompleted, Task>? Completed { get; set; }

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

            await NotifyAsync(new FileOperationCompleted([.. involvedPaths, .. result.Paths], result.Paths, refreshAll, result.Changes));
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
                await NotifyAsync(new FileOperationCompleted(involvedPaths, [], refreshAll, ex.Changes));
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("파일 작업 중 오류가 발생했습니다.", ex);
            _dialogs.ShowError("파일 작업", ex.Message);
            await NotifyAsync(new FileOperationCompleted(involvedPaths, [], refreshAll, []));
            return null;
        }
    }

    private async Task NotifyAsync(FileOperationCompleted completed)
    {
        if (Completed is not { } handler)
        {
            return;
        }

        try
        {
            await handler(completed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The operation itself succeeded: a refresh failure is only logged.
            _log.Error("파일 작업 후 트리를 새로 고치지 못했습니다.", ex);
        }
    }

    public Task<FileOperationResult?> UndoAsync() => RunAsync(s => s.Undo(), [], refreshAll: true);

    public Task<FileOperationResult?> RedoAsync() => RunAsync(s => s.Redo(), [], refreshAll: true);

    private static string FirstLine(string text) => text.Split('\n')[0];
}
