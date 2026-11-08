namespace DotDeck.Core.FileOperations;

/// <summary>Identifies one recycled item so it can be put back.</summary>
/// <param name="OriginalPath">Full path the item had before it was recycled.</param>
/// <param name="RecycledPath">Where the item now lives inside the recycle bin; null when it could not be located
/// (the item was recycled, but DotDeck cannot restore it — the user can still use the Windows recycle bin).</param>
public sealed record RecycleTicket(string OriginalPath, bool IsDirectory, string? RecycledPath)
{
    public bool CanRestore => RecycledPath is not null;
}

/// <summary>
/// The Windows recycle bin. Implementations: <c>WindowsRecycleBin</c> (App), a fake in tests.
/// DotDeck never deletes user files permanently by itself: where there is no recycle bin the operation is refused,
/// and if the shell would still delete permanently it must ask the user first.
/// </summary>
public interface IRecycleBin
{
    /// <summary>
    /// True when deleting <paramref name="path"/> would go to the recycle bin. False for locations without one
    /// (network shares, removable drives), where a delete would be permanent.
    /// </summary>
    bool CanRecycle(string path);

    /// <summary>Moves a file or folder to the recycle bin.</summary>
    /// <exception cref="IOException">The item was not recycled (it still exists).</exception>
    RecycleTicket Recycle(string path);

    /// <summary>True while the recycled item is still in the recycle bin (not emptied or restored elsewhere).</summary>
    bool Contains(RecycleTicket ticket);

    /// <summary>Moves the item back to <see cref="RecycleTicket.OriginalPath"/>. Never overwrites.</summary>
    /// <exception cref="IOException">The item could not be restored.</exception>
    void Restore(RecycleTicket ticket);
}
