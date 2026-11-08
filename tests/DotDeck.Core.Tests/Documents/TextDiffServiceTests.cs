using DotDeck.Core.Documents;

namespace DotDeck.Core.Tests.Documents;

public sealed class TextDiffServiceTests
{
    [Fact]
    public void Identical_texts_have_no_changes()
    {
        var diff = TextDiffService.Compute("a\nb", "a\nb");

        Assert.False(diff.HasChanges);
        Assert.All(diff.Lines, l => Assert.Equal(DiffLineKind.Unchanged, l.Kind));
    }

    [Fact]
    public void Replaced_line_has_old_and_new_numbers()
    {
        var diff = TextDiffService.Compute("a\nb\nc", "a\nB\nc\nd");

        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Unchanged, 1, 1, "a"),
                new DiffLine(DiffLineKind.Deleted, 2, null, "b"),
                new DiffLine(DiffLineKind.Inserted, null, 2, "B"),
                new DiffLine(DiffLineKind.Unchanged, 3, 3, "c"),
                new DiffLine(DiffLineKind.Inserted, null, 4, "d"),
            ],
            diff.Lines);
        Assert.Equal((2, 1), (diff.InsertedCount, diff.DeletedCount));
    }

    [Fact]
    public void Compact_keeps_context_and_folds_long_unchanged_runs()
    {
        var oldText = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"l{i}"));
        var newText = oldText.Replace("l10", "X10", StringComparison.Ordinal);

        var compact = TextDiffService.Compute(oldText, newText).Compact(context: 2);

        Assert.Equal(
            ["Gap:7", "Unchanged:l8", "Unchanged:l9", "Deleted:l10", "Inserted:X10", "Unchanged:l11", "Unchanged:l12", "Gap:8"],
            compact.Select(l => $"{l.Kind}:{l.Text}"));
    }

    [Fact]
    public void Compact_of_identical_texts_is_one_gap()
    {
        var compact = TextDiffService.Compute("a\nb", "a\nb").Compact();

        Assert.Equal([new DiffLine(DiffLineKind.Gap, null, null, "2")], compact);
    }

    [Fact]
    public void Empty_old_text_is_all_insertions()
    {
        var diff = TextDiffService.Compute(string.Empty, "x\ny");

        Assert.Equal(2, diff.InsertedCount);
        Assert.Equal(0, diff.DeletedCount);
    }
}
