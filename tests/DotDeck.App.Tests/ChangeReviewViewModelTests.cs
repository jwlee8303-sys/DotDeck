using DotDeck.App.ViewModels.Dialogs;
using DotDeck.Core.AiCli.Snapshots;

namespace DotDeck.App.Tests;

public sealed class ChangeReviewViewModelTests
{
    private readonly List<(string Path, bool Force)> _reverts = [];
    private readonly Queue<bool> _answers = new();
    private readonly List<string> _questions = [];
    private readonly Dictionary<string, RevertStatus> _firstResult = [];

    private static ChangeItem Item(string name, ChangeKind kind, bool revertible = true) => new()
    {
        Path = @"C:\work\" + name,
        Kind = kind,
        // A modified/deleted item without previous content is blocked; a created one never is.
        PreviousFile = revertible && kind != ChangeKind.Created ? new DotDeck.Core.Documents.BackupFile(@"C:\work\" + name, name, 1, "00", false) : null,
        PreviousSet = revertible && kind != ChangeKind.Created ? new DotDeck.Core.Documents.BackupSet("1", @"C:\b\1", DateTimeOffset.UnixEpoch, "pre-write", []) : null,
    };

    private ChangeReviewViewModel Create(params ChangeItem[] items) => new(
        "변경 검토",
        new ChangeSummary(items, IndexTruncated: false),
        item => new ChangeTexts("old\n", item.Path + "\n", null),
        (item, force) =>
        {
            _reverts.Add((item.Path, force));
            var status = !force && _firstResult.TryGetValue(item.Path, out var first) ? first : RevertStatus.Reverted;
            return new RevertResult(item, status, status == RevertStatus.Reverted ? null : "x");
        },
        (_, question) =>
        {
            _questions.Add(question);
            return _answers.Count > 0 && _answers.Dequeue();
        });

    [Fact]
    public void Rows_show_kind_reason_and_lazy_diff()
    {
        var vm = Create(Item("a.md", ChangeKind.Modified), Item("big.bin", ChangeKind.Modified, revertible: false), Item("new.md", ChangeKind.Created));

        Assert.Equal(["수정", "수정", "생성"], vm.Rows.Select(r => r.KindText));
        Assert.False(vm.Rows[1].CanRevert);
        Assert.NotNull(vm.Rows[1].BlockedReason);
        Assert.Contains(vm.Rows[0].Lines, l => l.Kind == DotDeck.Core.Documents.DiffLineKind.Inserted);
        Assert.True(vm.HasRevertible);
        Assert.Contains("변경 3개", vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_selected_asks_before_overwriting_a_file_changed_since_the_summary()
    {
        var vm = Create(Item("a.md", ChangeKind.Modified));
        _firstResult[@"C:\work\a.md"] = RevertStatus.ChangedSinceSummary;

        _answers.Enqueue(false);
        vm.RevertSelectedCommand.Execute(null);
        Assert.Equal([(@"C:\work\a.md", false)], _reverts);
        Assert.False(vm.Rows[0].IsDone);
        Assert.Contains("건너뜀", vm.Rows[0].Status, StringComparison.Ordinal);

        _answers.Enqueue(true);
        vm.RevertSelectedCommand.Execute(null);
        Assert.Equal((@"C:\work\a.md", true), _reverts[^1]);
        Assert.True(vm.Rows[0].IsDone);
        Assert.False(vm.RevertSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void Revert_all_confirms_once_and_asks_again_only_for_files_changed_since()
    {
        var vm = Create(Item("a.md", ChangeKind.Modified), Item("b.md", ChangeKind.Deleted), Item("c.md", ChangeKind.Created), Item("x.bin", ChangeKind.Modified, revertible: false));
        _firstResult[@"C:\work\b.md"] = RevertStatus.ChangedSinceSummary;
        _answers.Enqueue(true);  // revert all?
        _answers.Enqueue(false); // overwrite b.md changed since? no

        vm.RevertAllCommand.Execute(null);

        Assert.Equal(2, _questions.Count);
        Assert.Contains(@"C:\work\b.md", _questions[1], StringComparison.Ordinal);
        Assert.DoesNotContain(_reverts, r => r.Path.EndsWith("x.bin", StringComparison.Ordinal));
        Assert.DoesNotContain(_reverts, r => r.Force);
        Assert.Equal([true, false, true, false], vm.Rows.Select(r => r.IsDone));
        Assert.Equal("휴지통으로 이동함", vm.Rows[2].Status);
    }

    [Fact]
    public void Revert_all_does_nothing_when_not_confirmed()
    {
        var vm = Create(Item("a.md", ChangeKind.Modified));

        vm.RevertAllCommand.Execute(null);

        Assert.Empty(_reverts);
        Assert.Single(_questions);
    }

    [Fact]
    public void Accept_closes_without_reverting()
    {
        var vm = Create(Item("a.md", ChangeKind.Modified));
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.AcceptCommand.Execute(null);

        Assert.True(closed);
        Assert.Empty(_reverts);
    }

    [Fact]
    public void Empty_summary_says_nothing_changed()
    {
        var vm = Create();

        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRevertible);
        Assert.Contains("바꾸지 않았습니다", vm.Message, StringComparison.Ordinal);
    }
}
