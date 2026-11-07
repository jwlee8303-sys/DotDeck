using System.IO;
using DotDeck.App.ViewModels.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.Logging;

namespace DotDeck.App.ViewModels;

/// <summary>Open editor tabs saved in session.json and recreated before the docking layout is restored.</summary>
public sealed partial class ShellViewModel
{
    /// <summary>
    /// Reopens the tabs of the last session (with their view numbers, so layout ContentIds match). Missing,
    /// secret, binary and large files are skipped; the layout then simply drops their tabs.
    /// </summary>
    public async Task RestoreDocumentsAsync()
    {
        var saved = _session.LoadDocuments();
        var skipped = 0;
        var previewUsed = false;
        foreach (var group in saved.GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var probe = await Task.Run(() => _buffers.Reader.Probe(group.Key)).ConfigureAwait(true);
                if (probe.IsBinary || probe.IsLarge)
                {
                    skipped++;
                    continue;
                }

                var buffer = await _buffers.AcquireAsync(group.Key).ConfigureAwait(true);
                if (Documents.OfType<TextDocumentViewModel>().Any(d => d.Buffer == buffer))
                {
                    // Opened meanwhile (explorer click during startup): keep that tab only.
                    _buffers.Release(buffer);
                    continue;
                }

                var first = true;
                foreach (var state in group.OrderBy(d => d.ViewNumber))
                {
                    if (!first)
                    {
                        _buffers.AddReference(buffer);
                    }

                    first = false;
                    var preview = state.IsPreview && !state.IsPinned && !previewUsed && PreviewDocument is null;
                    previewUsed |= preview;
                    var document = CreateTextDocument(buffer, preview, state.ViewNumber);
                    document.IsPinned = state.IsPinned;
                    document.IsMarkdownPreviewOpen = state.IsMarkdownPreviewOpen && document.IsMarkdown;
                    document.CaretLine = state.CaretLine;
                    document.CaretColumn = state.CaretColumn;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                skipped++;
                _log.Warning($"Session tab not restored: {group.Key} ({ex.GetType().Name})");
            }
        }

        if (skipped > 0)
        {
            StatusMessage = $"지난 세션의 탭 {skipped}개는 열 수 없어 복원하지 않았습니다(없음·비밀·바이너리·큰 파일).";
        }
    }

    /// <summary>Saves view state that is not part of the docking layout (called on close).</summary>
    public void SaveSession()
    {
        Projects.SaveState();
        _session.SaveDocuments(Documents.OfType<TextDocumentViewModel>()
            .Select(d => new DocumentSessionState(
                d.FilePath, d.ViewNumber, d.IsPinned, d.IsPreview, d.IsMarkdownPreviewOpen, d.CaretLine, d.CaretColumn))
            .ToList());
    }
}
