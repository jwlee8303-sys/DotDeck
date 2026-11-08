using DotDeck.Core.Files;

namespace DotDeck.Core.Explorer;

public enum NodeKind
{
    Folder,
    File,

    /// <summary>A node not backed by a file or folder (System project entries).</summary>
    Virtual,
}

/// <summary>
/// Immutable description of one tree node. The UI wraps it in a row view model; refreshing replaces the
/// <see cref="NodeInfo"/> while expansion state is kept by <see cref="Id"/>.
/// </summary>
public sealed record NodeInfo
{
    /// <summary>Stable key: the full path for file system nodes, "system:&lt;key&gt;" for virtual nodes.</summary>
    public required string Id { get; init; }

    public required NodeKind Kind { get; init; }

    public required string Name { get; init; }

    /// <summary>Absolute path; null for virtual nodes.</summary>
    public string? FullPath { get; init; }

    /// <summary>Path relative to the project root ("" for the root itself); null for virtual nodes.</summary>
    public string? RelativePath { get; init; }

    public FileKind FileKind { get; init; }

    /// <summary>Secret file: shown with a lock icon and never opened.</summary>
    public bool IsSecret { get; init; }

    public bool IsHidden { get; init; }

    public bool IsReadOnly { get; init; }

    /// <summary>Junction or symbolic link.</summary>
    public bool IsLink { get; init; }

    /// <summary>False for placeholder nodes that cannot be used yet (System project in Phase 1, decision Q14).</summary>
    public bool IsEnabled { get; init; } = true;

    public long Length { get; init; }

    public DateTime LastWriteTimeUtc { get; init; }

    /// <summary>Compact tree: the folders merged into this row, first to last (this node is the last). Null otherwise.</summary>
    public IReadOnlyList<NodeInfo>? Chain { get; init; }

    /// <summary>Flat view: the folder of the file relative to the project root ("" at the root). Null otherwise.</summary>
    public string? FlatDirectory { get; init; }

    /// <summary>Pinned to the top of the project section.</summary>
    public bool IsPinned { get; init; }

    public bool CanExpand => Kind == NodeKind.Folder;

    public bool CanOpen => Kind == NodeKind.File && !IsSecret;

    public static NodeInfo FromEntry(FileSystemEntry entry, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(relativePath);
        var isFile = !entry.IsDirectory;
        return new NodeInfo
        {
            Id = entry.FullPath,
            Kind = isFile ? NodeKind.File : NodeKind.Folder,
            Name = entry.Name,
            FullPath = entry.FullPath,
            RelativePath = relativePath,
            FileKind = isFile ? FileKindClassifier.Classify(entry.Name) : FileKind.Other,
            IsSecret = isFile && SecretFilePolicy.IsSecret(entry.Name),
            IsHidden = entry.IsHidden,
            IsReadOnly = entry.IsReadOnly,
            IsLink = entry.IsReparsePoint,
            Length = entry.Length,
            LastWriteTimeUtc = entry.LastWriteTimeUtc,
        };
    }
}
