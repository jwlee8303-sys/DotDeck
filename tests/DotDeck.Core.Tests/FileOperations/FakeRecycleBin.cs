using DotDeck.Core.FileOperations;

namespace DotDeck.Core.Tests.FileOperations;

/// <summary>Recycle bin that moves items into a folder of the test's temp directory.</summary>
internal sealed class FakeRecycleBin(string binDirectory) : IRecycleBin
{
    /// <summary>Paths under these folders behave like a drive without a recycle bin.</summary>
    public List<string> NoRecycleBinFolders { get; } = [];

    /// <summary>When true, recycled items are not located (ticket cannot restore), like an unreadable $I.</summary>
    public bool LoseTrack { get; set; }

    public int RecycleCount { get; private set; }

    public bool CanRecycle(string path) =>
        !NoRecycleBinFolders.Any(f => Path.GetFullPath(path).StartsWith(Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase));

    public RecycleTicket Recycle(string path)
    {
        if (!CanRecycle(path))
        {
            throw new InvalidOperationException("Test bug: recycling where CanRecycle is false.");
        }

        Directory.CreateDirectory(binDirectory);
        var isDirectory = Directory.Exists(path);
        var target = Path.Combine(binDirectory, "$R" + Guid.NewGuid().ToString("N")[..6]);
        if (isDirectory)
        {
            Directory.Move(path, target);
        }
        else
        {
            File.Move(path, target);
        }

        RecycleCount++;
        return new RecycleTicket(path, isDirectory, LoseTrack ? null : target);
    }

    public bool Contains(RecycleTicket ticket) =>
        ticket.RecycledPath is { } p && (ticket.IsDirectory ? Directory.Exists(p) : File.Exists(p));

    public void Restore(RecycleTicket ticket)
    {
        if (ticket.IsDirectory)
        {
            Directory.Move(ticket.RecycledPath!, ticket.OriginalPath);
        }
        else
        {
            File.Move(ticket.RecycledPath!, ticket.OriginalPath, overwrite: false);
        }
    }

    /// <summary>Simulates the user emptying the recycle bin.</summary>
    public void Empty()
    {
        if (Directory.Exists(binDirectory))
        {
            Directory.Delete(binDirectory, recursive: true);
        }
    }
}
