using System.Buffers.Binary;
using System.Text;

namespace DotDeck.Core.FileOperations;

/// <summary>Contents of a recycle bin "$I" metadata file.</summary>
/// <param name="Size">Size in bytes of the recycled item.</param>
public sealed record RecycleBinInfo(int Version, long Size, DateTime DeletedUtc, string OriginalPath);

/// <summary>
/// Parses <c>$Recycle.Bin\&lt;SID&gt;\$I*</c> files (undocumented; DESIGN Q7). Layout, little endian:
/// v1 (Vista–8.1): int64 version, int64 size, FILETIME deleted, 260 UTF-16 chars path (null padded).
/// v2 (Windows 10+): int64 version, int64 size, FILETIME deleted, int32 path length in chars (including the
/// terminating null), UTF-16 path. Anything else is rejected rather than guessed.
/// </summary>
public static class RecycleBinInfoFile
{
    private const int HeaderLength = 24;
    private const int V1PathChars = 260;

    /// <summary>Returns null when the data is not a recognized $I file.</summary>
    public static RecycleBinInfo? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength)
        {
            return null;
        }

        var version = BinaryPrimitives.ReadInt64LittleEndian(data);
        var size = BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(data[16..]);
        if (size < 0 || fileTime < 0)
        {
            return null;
        }

        ReadOnlySpan<byte> pathBytes;
        switch (version)
        {
            case 1:
                if (data.Length < HeaderLength + (V1PathChars * 2))
                {
                    return null;
                }

                pathBytes = data.Slice(HeaderLength, V1PathChars * 2);
                break;

            case 2:
                if (data.Length < HeaderLength + 4)
                {
                    return null;
                }

                var chars = BinaryPrimitives.ReadInt32LittleEndian(data[HeaderLength..]);
                if (chars <= 0 || chars > 32_768 || data.Length < HeaderLength + 4 + ((long)chars * 2))
                {
                    return null;
                }

                pathBytes = data.Slice(HeaderLength + 4, chars * 2);
                break;

            default:
                return null;
        }

        var path = Encoding.Unicode.GetString(pathBytes);
        var nul = path.IndexOf('\0', StringComparison.Ordinal);
        if (nul >= 0)
        {
            path = path[..nul];
        }

        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        DateTime deleted;
        try
        {
            deleted = DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        return new RecycleBinInfo((int)version, size, deleted, path);
    }

    /// <summary>Builds a v2 file (used by tests and by fakes).</summary>
    public static byte[] CreateV2(long size, DateTime deletedUtc, string originalPath)
    {
        ArgumentNullException.ThrowIfNull(originalPath);
        var chars = originalPath.Length + 1;
        var data = new byte[HeaderLength + 4 + (chars * 2)];
        BinaryPrimitives.WriteInt64LittleEndian(data, 2);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), size);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), deletedUtc.ToFileTimeUtc());
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(HeaderLength), chars);
        Encoding.Unicode.GetBytes(originalPath, data.AsSpan(HeaderLength + 4));
        return data;
    }
}
