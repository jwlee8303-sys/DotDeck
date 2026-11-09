using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.AiCli.Snapshots;
using DotDeck.Core.Documents;

namespace DotDeck.App.ViewModels.Dialogs;

/// <summary>Text of both sides of one change, or why it cannot be shown.</summary>
public sealed record ChangeTexts(string? Before, string? After, string? Note);

/// <summary>One changed file of the review.</summary>
public sealed partial class ChangeRowViewModel : ObservableObject
{
    private readonly Func<ChangeItem, ChangeTexts> _load;
    private IReadOnlyList<DiffLine>? _lines;

    internal ChangeRowViewModel(ChangeItem item, Func<ChangeItem, ChangeTexts> load)
    {
        Item = item;
        _load = load;
    }

    public ChangeItem Item { get; }

    public string Path => Item.Path;

    public string Name => System.IO.Path.GetFileName(Item.Path);

    public string KindText => Item.Kind switch
    {
        ChangeKind.Created => "생성",
        ChangeKind.Deleted => "삭제",
        _ => "수정",
    };

    /// <summary>Why the file cannot be reverted (null when it can).</summary>
    public string? BlockedReason => Item.RevertBlockedReason;

    public bool CanRevert => BlockedReason is null && !IsDone;

    /// <summary>Result of the last revert attempt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    private bool _isDone;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _note;

    /// <summary>Screen share mode hides the diff body until the eye button is pressed (as in other diff reviews).</summary>
    public bool CanReveal { get; } = DotDeck.Core.Security.SecretPresentation.ScreenShareMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Lines))]
    private bool _isRevealed = !DotDeck.Core.Security.SecretPresentation.ScreenShareMode;

    [RelayCommand]
    private void Reveal() => IsRevealed = !IsRevealed;

    /// <summary>Diff from the previous content to the current one (what the session did), loaded on first view.</summary>
    public IReadOnlyList<DiffLine> Lines
    {
        get
        {
            if (_lines is null)
            {
                var texts = _load(Item);
                Note = texts.Note;
                _lines = texts.Before is null && texts.After is null
                    ? []
                    : TextDiffService.Compute(texts.Before ?? string.Empty, texts.After ?? string.Empty).Compact();
            }

            return IsRevealed
                ? _lines
                : [.. _lines.Select(l => l.Kind == DiffLineKind.Gap ? l : l with { Text = DotDeck.Core.Security.SecretMasker.Mask })];
        }
    }
}

/// <summary>
/// Change summary after an AI session (SPEC 11.3-4): changed, created and deleted files with diffs, revert per file,
/// revert all, or accept. Reverting goes through <see cref="RevertPlanner"/> (backups first, created files to the
/// recycle bin); a file edited again since the summary is overwritten only after asking.
/// </summary>
public sealed partial class ChangeReviewViewModel : ObservableObject
{
    private readonly Func<ChangeItem, bool, RevertResult> _revert;
    private readonly Func<string, string, bool> _confirm;

    public ChangeReviewViewModel(
        string title,
        ChangeSummary summary,
        Func<ChangeItem, ChangeTexts> load,
        Func<ChangeItem, bool, RevertResult> revert,
        Func<string, string, bool> confirm)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(load);
        Title = title;
        _revert = revert ?? throw new ArgumentNullException(nameof(revert));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        Rows = [.. summary.Items.Select(i => new ChangeRowViewModel(i, load))];
        _selectedRow = Rows.FirstOrDefault();
        Message = summary.Items.Count == 0
            ? "세션이 범위 안 파일을 바꾸지 않았습니다."
            : $"변경 {summary.Items.Count}개" + (summary.IndexTruncated ? " · 색인이 일부만 만들어져 놓친 변경이 있을 수 있습니다." : string.Empty);
    }

    public string Title { get; }

    public string Message { get; }

    public ObservableCollection<ChangeRowViewModel> Rows { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommand))]
    private ChangeRowViewModel? _selectedRow;

    public bool HasRevertible => Rows.Any(r => r.CanRevert);

    public event EventHandler? CloseRequested;

    private bool CanRevertSelected() => SelectedRow?.CanRevert == true;

    [RelayCommand(CanExecute = nameof(CanRevertSelected))]
    private void RevertSelected()
    {
        if (SelectedRow is { } row)
        {
            Revert(row, askForChanged: true);
            RefreshCommands();
        }
    }

    [RelayCommand]
    private void RevertAll()
    {
        var rows = Rows.Where(r => r.CanRevert).ToList();
        if (rows.Count == 0
            || !_confirm("모두 되돌리기", $"{rows.Count}개 파일을 세션 전 상태로 되돌립니다. 새로 만든 파일은 휴지통으로 이동합니다.\n\n계속할까요?"))
        {
            return;
        }

        var changed = new List<ChangeRowViewModel>();
        foreach (var row in rows)
        {
            if (Revert(row, askForChanged: false) == RevertStatus.ChangedSinceSummary)
            {
                changed.Add(row);
            }
        }

        // Files edited again after the summary are asked about together, never overwritten silently.
        if (changed.Count > 0
            && _confirm("모두 되돌리기", $"요약 이후 다시 바뀐 파일 {changed.Count}개가 있습니다:\n{string.Join("\n", changed.Select(r => r.Path))}\n\n지금 내용을 버리고 되돌릴까요? (지금 내용은 백업됩니다)"))
        {
            foreach (var row in changed)
            {
                Apply(row, _revert(row.Item, true));
            }
        }

        RefreshCommands();
    }

    [RelayCommand]
    private void Accept() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private RevertStatus Revert(ChangeRowViewModel row, bool askForChanged)
    {
        var result = _revert(row.Item, false);
        if (result.Status == RevertStatus.ChangedSinceSummary && askForChanged
            && _confirm("되돌리기", $"{row.Path}\n\n요약 이후 파일이 다시 바뀌었습니다. 지금 내용을 버리고 되돌릴까요? (지금 내용은 백업됩니다)"))
        {
            result = _revert(row.Item, true);
        }

        Apply(row, result);
        return result.Status;
    }

    private static void Apply(ChangeRowViewModel row, RevertResult result)
    {
        row.IsDone = result.Status == RevertStatus.Reverted;
        row.Status = result.Status switch
        {
            RevertStatus.Reverted => row.Item.Kind == ChangeKind.Created ? "휴지통으로 이동함" : "되돌림",
            RevertStatus.ChangedSinceSummary => "요약 이후 바뀌어 건너뜀",
            RevertStatus.NotRevertible => "되돌릴 수 없음: " + result.Message,
            _ => "실패: " + result.Message,
        };
    }

    private void RefreshCommands()
    {
        RevertSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasRevertible));
    }
}
