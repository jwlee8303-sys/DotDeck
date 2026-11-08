namespace DotDeck.Core.FileOperations;

/// <summary>A file operation was refused or failed. <see cref="Exception.Message"/> is shown to the user (Korean).</summary>
public sealed class FileOperationException : Exception
{
    public FileOperationException(string message, bool partiallyCompleted = false, Exception? innerException = null)
        : base(message, innerException)
    {
        PartiallyCompleted = partiallyCompleted;
    }

    /// <summary>Some items were processed before the failure; that part is in the history and can be undone.</summary>
    public bool PartiallyCompleted { get; }

    /// <summary>What the processed part changed on disk (so rules and tabs can follow it).</summary>
    public IReadOnlyList<PathChange> Changes { get; init; } = [];
}
