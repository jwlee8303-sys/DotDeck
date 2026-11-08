namespace DotDeck.Core.FileOperations;

/// <summary>A change to the file system made by an operation, undo or redo (absolute paths).</summary>
public abstract record PathChange
{
    private PathChange()
    {
    }

    /// <summary>Moved or renamed (a folder takes everything below it along).</summary>
    public sealed record Moved(string From, string To) : PathChange;

    /// <summary>Sent to the recycle bin.</summary>
    public sealed record Removed(string Path) : PathChange;

    /// <summary>Created, copied, or restored from the recycle bin.</summary>
    public sealed record Created(string Path) : PathChange;
}
