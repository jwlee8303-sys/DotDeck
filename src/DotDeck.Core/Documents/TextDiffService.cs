using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace DotDeck.Core.Documents;

public enum DiffLineKind
{
    Unchanged,
    Inserted,
    Deleted,

    /// <summary>Placeholder for unchanged lines hidden by <see cref="TextDiff.Compact"/>; Text holds the count.</summary>
    Gap,
}

/// <summary>One line of a unified diff. Line numbers are 1-based; the side a line does not exist on is null.</summary>
public sealed record DiffLine(DiffLineKind Kind, int? OldLineNumber, int? NewLineNumber, string Text);

/// <summary>Line-based diff between two texts. Line endings are not compared (see <see cref="WritePlan.LineEndingsChanged"/>).</summary>
public sealed record TextDiff(IReadOnlyList<DiffLine> Lines, int InsertedCount, int DeletedCount)
{
    public bool HasChanges => InsertedCount + DeletedCount > 0;

    /// <summary>
    /// Keeps changed lines plus <paramref name="context"/> unchanged lines around them; each longer unchanged run
    /// becomes one <see cref="DiffLineKind.Gap"/> line whose text is the number of hidden lines.
    /// </summary>
    public IReadOnlyList<DiffLine> Compact(int context = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(context);
        var keep = new bool[Lines.Count];
        for (var i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].Kind is DiffLineKind.Inserted or DiffLineKind.Deleted)
            {
                for (var j = Math.Max(0, i - context); j <= Math.Min(Lines.Count - 1, i + context); j++)
                {
                    keep[j] = true;
                }
            }
        }

        var result = new List<DiffLine>();
        var hidden = 0;
        for (var i = 0; i < Lines.Count; i++)
        {
            if (keep[i])
            {
                FlushGap();
                result.Add(Lines[i]);
            }
            else
            {
                hidden++;
            }
        }

        FlushGap();
        return result;

        void FlushGap()
        {
            if (hidden > 0)
            {
                result.Add(new DiffLine(DiffLineKind.Gap, null, null, hidden.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                hidden = 0;
            }
        }
    }
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
