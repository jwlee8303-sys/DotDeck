using System.IO;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Dialogs;
using DotDeck.Core.AiCli.Sessions;
using DotDeck.Core.AiCli.Snapshots;
using DotDeck.Core.Documents;
using DotDeck.Core.Logging;

namespace DotDeck.App.ViewModels;

/// <summary>"AI 작업" panel and the change summary (SPEC 11.3-4): review, revert, and the review offered at session end.</summary>
public sealed partial class ShellViewModel
{
    private readonly HashSet<string> _aiEndSeen = new(StringComparer.Ordinal);

    /// <summary>Shows the "AI 작업" panel (status bar click).</summary>
    [RelayCommand]
    private void ShowAiWork()
    {
        AiWork.IsVisible = true;
        AiWork.IsActive = true;
    }

    private bool ConfirmStopAiSession(AiSession session) =>
        _dialogs.Confirm("세션 중지", $"{session.Title} 세션을 중지할까요?\n\n진행 중인 작업이 끊깁니다. 바뀐 파일은 변경 검토에서 확인할 수 있습니다.");

    /// <summary>Called on the UI thread whenever the manager changes: offers the review once per finished session.</summary>
    private void OfferReviewForFinishedSessions()
    {
        if (_aiSessions is null)
        {
            return;
        }

        foreach (var session in _aiSessions.Sessions.Where(s => s.State != AiSessionState.Running && _aiEndSeen.Add(s.Id)))
        {
            _aiPendingReviews.Enqueue(session);
        }

        if (!_aiReviewLoopRunning && _aiPendingReviews.Count > 0)
        {
            _ = RunPendingReviewsAsync();
        }
    }

    private readonly Queue<AiSession> _aiPendingReviews = new();
    private bool _aiReviewLoopRunning;

    /// <summary>One automatic review at a time, in the order sessions ended (all on the UI thread).</summary>
    private async Task RunPendingReviewsAsync()
    {
        _aiReviewLoopRunning = true;
        try
        {
            while (_aiPendingReviews.TryDequeue(out var session))
            {
                await ReviewAiChangesAsync(session, onlyWhenChanged: true).ConfigureAwait(true);
            }
        }
        finally
        {
            _aiReviewLoopRunning = false;
        }
    }

    internal Task ReviewAiChangesAsync(AiSession session) => ReviewAiChangesAsync(session, onlyWhenChanged: false);

    private async Task ReviewAiChangesAsync(AiSession session, bool onlyWhenChanged)
    {
        if (_aiSessions is not { } manager)
        {
            return;
        }

        ChangeSummary? summary;
        try
        {
            summary = await Task.Run(() => manager.BuildChangeSummary(session)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Notify(onlyWhenChanged, isError: true, $"{session.Title}: 변경 요약을 만들지 못했습니다: {ex.Message}");
            return;
        }

        if (summary is null)
        {
            Notify(onlyWhenChanged, isError: false, $"{session.Title}: 시작 스냅샷이 없어 변경을 비교할 수 없습니다.");
            return;
        }

        if (summary.Items.Count == 0 && onlyWhenChanged)
        {
            StatusMessage = $"{session.Title} 세션이 끝났습니다. 바뀐 파일이 없습니다.";
            return;
        }

        // Never stack a modal dialog on another one (checked after the summary was built): the panel button stays.
        if (onlyWhenChanged && _dialogs.IsShowingDialog)
        {
            StatusMessage = $"{session.Title} 세션이 끝났습니다. AI 작업 패널의 \"변경 검토\"로 확인하세요.";
            return;
        }

        var backups = manager.OpenBackups(session);
        var planner = manager.CreateRevertPlanner(session, _writer);
        var review = new ChangeReviewViewModel(
            $"변경 검토 — {session.Title}",
            summary,
            item => LoadChangeTexts(item, backups),
            (item, force) =>
            {
                var result = planner.Revert(item, force);
                if (result.Status is RevertStatus.Reverted)
                {
                    _log.Info($"AI 변경 되돌림: {item.Path}");
                }
                else if (result.Status is RevertStatus.Failed)
                {
                    _log.Warning($"AI 변경 되돌리기 실패: {item.Path} ({result.Message})");
                }

                return result;
            },
            _dialogs.Confirm);
        _dialogs.ShowChangeReview(review);

        // Open documents of reverted files pick up the restored content through the usual external-change check.
        _ = CheckExternalChangesAsync(deep: true);
    }

    /// <summary>
    /// A message about a review. Automatic reviews (at session end) never open a modal over another one; they fall
    /// back to the status bar and the log.
    /// </summary>
    private void Notify(bool automatic, bool isError, string message)
    {
        if (automatic && _dialogs.IsShowingDialog)
        {
            StatusMessage = message;
            if (isError)
            {
                _log.Warning(message);
            }
        }
        else if (isError)
        {
            _dialogs.ShowError("변경 검토", message);
        }
        else
        {
            _dialogs.ShowInfo("변경 검토", message);
        }
    }

    /// <summary>Both sides of a change as text, or a note why they are not shown (secret, binary, too large).</summary>
    private ChangeTexts LoadChangeTexts(ChangeItem item, BackupStore backups)
    {
        const string secretNote = "비밀 파일은 내용을 비교해 보여 주지 않습니다.";
        if (item.IsSecret)
        {
            return new ChangeTexts(null, null, secretNote);
        }

        try
        {
            // The name may hide a link to a secret file (notes.txt -> .env): check the final target before reading.
            // A chain that cannot be resolved counts as secret.
            if (item.Kind != ChangeKind.Deleted && _store.GetEntry(item.Path) is not null
                && DotDeck.Core.Explorer.SecretFileResolver.IsSecretFile(_store, item.Path))
            {
                return new ChangeTexts(null, null, secretNote);
            }

            var before = item.PreviousSet is { } set && item.PreviousFile is { } file ? Decode(item.Path, backups.ReadContent(set, file)) : null;
            var after = item.Kind == ChangeKind.Deleted ? null : ReadCurrent(item.Path);
            if (before is { Binary: true } || after is { Binary: true })
            {
                return new ChangeTexts(null, null, "바이너리 파일이라 내용을 비교하지 않습니다.");
            }

            if (after is { TooLarge: true })
            {
                return new ChangeTexts(null, null, "파일이 커서 내용을 비교하지 않습니다.");
            }

            var note = item.PreviousFile is null && item.Kind != ChangeKind.Created ? "이전 내용이 기록되지 않아 현재 내용만 보입니다." : null;
            return new ChangeTexts(before?.Text, after?.Text, note);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new ChangeTexts(null, null, $"내용을 읽지 못했습니다: {ex.Message}");
        }
    }

    private sealed record SideText(string? Text, bool Binary = false, bool TooLarge = false);

    private SideText? ReadCurrent(string path)
    {
        var entry = _store.GetEntry(path);
        if (entry is null || entry.IsDirectory)
        {
            return null;
        }

        return entry.Length > new SnapshotLimits().MaxContentBytes ? new SideText(null, TooLarge: true) : Decode(path, _store.ReadAllBytes(path));
    }

    private static SideText Decode(string path, byte[] bytes) =>
        TextFileReader.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8192)))
            ? new SideText(null, Binary: true)
            : new SideText(TextFileReader.Decode(path, bytes, DateTime.MinValue).Text);
}
