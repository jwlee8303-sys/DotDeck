using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace DotDeck.Core.Documents;

public enum DiffLineKind
{
    Unchanged,
    Inserted,
    Deleted,
}

/// <summary>One line of a unified diff. Line numbers are 1-based; the side a line does not exist on is null.</summary>
public sealed record DiffLine(DiffLineKind Kind, int? OldLineNumber, int? NewLineNumber, string Text);

/// <summary>Line-based diff between two texts. Line endings are not compared (see <see cref="WritePlan.LineEndingsChanged"/>).</summary>
public sealed record TextDiff(IReadOnlyList<DiffLine> Lines, int InsertedCount, int DeletedCount)
{
    public bool HasChanges => InsertedCount + DeletedCount > 0;
}

/// <summary>Builds line diffs with DiffPlex.</summary>
public static class TextDiffService
{
    private static readonly InlineDiffBuilder s_builder = new(new Differ());

    public static TextDiff Compute(string oldText, string newText)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);

        var model = s_builder.BuildDiffModel(oldText, newText, ignoreWhitespace: false);
        var lines = new List<DiffLine>(model.Lines.Count);
        int oldNumber = 0, newNumber = 0, inserted = 0, deleted = 0;
        foreach (var piece in model.Lines)
        {
            switch (piece.Type)
            {
                case ChangeType.Inserted:
                    inserted++;
                    lines.Add(new DiffLine(DiffLineKind.Inserted, null, ++newNumber, piece.Text ?? string.Empty));
                    break;
                case ChangeType.Deleted:
                    deleted++;
                    lines.Add(new DiffLine(DiffLineKind.Deleted, ++oldNumber, null, piece.Text ?? string.Empty));
                    break;
                case ChangeType.Imaginary:
                    break;
                default:
                    lines.Add(new DiffLine(DiffLineKind.Unchanged, ++oldNumber, ++newNumber, piece.Text ?? string.Empty));
                    break;
            }
        }

        return new TextDiff(lines, inserted, deleted);
    }
}
