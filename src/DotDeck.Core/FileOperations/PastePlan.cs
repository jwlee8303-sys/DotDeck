namespace DotDeck.Core.FileOperations;

/// <summary>What the explorer selection is when pasting.</summary>
public enum PasteSelectionKind
{
    None,
    Folder,
    File,

    /// <summary>The project header row: paste into the project root.</summary>
    ProjectRoot,
}

/// <param name="FullPath">The real path of the selected item (never its position on screen).</param>
/// <param name="ChainFolders">Compact tree: the merged folders of the row, first to last.</param>
/// <param name="ActiveSegment">Compact tree: the index of the clicked path segment, or null.</param>
public sealed record PasteSelection(PasteSelectionKind Kind, string? FullPath, IReadOnlyList<string>? ChainFolders = null, int? ActiveSegment = null);

/// <summary>Where Ctrl+V puts items (SPEC 2.3 "붙여넣기·복제 위치").</summary>
public static class PasteTargetResolver
{
    public const string NoSelectionMessage = "붙여넣을 위치(폴더나 파일)를 먼저 선택하세요.";

    /// <summary>
    /// Folder → itself; file → its folder; merged compact row → the clicked segment, else the last folder;
    /// project header → the root.
    /// </summary>
    /// <exception cref="FileOperationException">Nothing usable is selected.</exception>
    public static string Resolve(PasteSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.FullPath is not { Length: > 0 } path)
        {
            throw new FileOperationException(NoSelectionMessage);
        }

        return selection.Kind switch
        {
            PasteSelectionKind.Folder when selection.ChainFolders is { Count: > 0 } chain =>
                selection.ActiveSegment is { } index && index >= 0 && index < chain.Count ? chain[index] : chain[^1],
            PasteSelectionKind.Folder or PasteSelectionKind.ProjectRoot => path,
            PasteSelectionKind.File => Path.GetDirectoryName(path) ?? throw new FileOperationException(NoSelectionMessage),
            _ => throw new FileOperationException(NoSelectionMessage),
        };
    }
}

public enum PasteItemKind
{
    /// <summary>The name is free in the target folder.</summary>
    Free,

    /// <summary>Another item with this name is there: the user chooses (overwrite / keep both / skip).</summary>
    Conflict,

    /// <summary>Copy into its own folder: handled like duplicate ("이름 - 복사본").</summary>
    SameFolder,

    /// <summary>Cut and pasted into its own folder: nothing to do.</summary>
    AlreadyThere,
}

public enum ConflictChoice
{
    Overwrite,
    KeepBoth,
    Skip,
}

public sealed record PastePlanItem(string Source, string Destination, bool IsDirectory, PasteItemKind Kind)
{
    /// <summary>Overwriting is offered only when a file replaces a file.</summary>
    public bool CanOverwrite { get; init; }
}

public sealed record PastePlan(string TargetDirectory, bool IsMove, IReadOnlyList<PastePlanItem> Items)
{
    public IEnumerable<PastePlanItem> Conflicts => Items.Where(i => i.Kind == PasteItemKind.Conflict);
}
