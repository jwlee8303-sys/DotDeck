using System.Buffers.Binary;
using System.Text;
using DotDeck.Core.FileOperations;

namespace DotDeck.Core.Tests.FileOperations;

public sealed class RecycleBinInfoFileTests
{
    private static readonly DateTime s_deleted = new(2026, 11, 7, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public void Parses_version_2()
    {
        var data = RecycleBinInfoFile.CreateV2(1234, s_deleted, @"D:\작업\a.md");

        var info = RecycleBinInfoFile.TryParse(data);

        Assert.NotNull(info);
        Assert.Equal(2, info.Version);
        Assert.Equal(1234, info.Size);
        Assert.Equal(s_deleted, info.DeletedUtc);
        Assert.Equal(@"D:\작업\a.md", info.OriginalPath);
    }

    [Fact]
    public void Parses_version_1()
    {
        var data = new byte[24 + 520];
        BinaryPrimitives.WriteInt64LittleEndian(data, 1);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), 10);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), s_deleted.ToFileTimeUtc());
        Encoding.Unicode.GetBytes(@"C:\x\y.txt", data.AsSpan(24));

        var info = RecycleBinInfoFile.TryParse(data);

        Assert.NotNull(info);
        Assert.Equal(@"C:\x\y.txt", info.OriginalPath);
    }

    [Fact]
    public void Rejects_unknown_versions_and_truncated_data()
    {
        var data = RecycleBinInfoFile.CreateV2(1, s_deleted, @"C:\a");
        var unknown = (byte[])data.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(unknown, 3);

        Assert.Null(RecycleBinInfoFile.TryParse(unknown));
        Assert.Null(RecycleBinInfoFile.TryParse(data.AsSpan(0, data.Length - 2)));
        Assert.Null(RecycleBinInfoFile.TryParse(data.AsSpan(0, 10)));
        Assert.Null(RecycleBinInfoFile.TryParse([]));
    }

    [Fact]
    public void Rejects_relative_or_empty_paths_and_bad_lengths()
    {
        Assert.Null(RecycleBinInfoFile.TryParse(RecycleBinInfoFile.CreateV2(1, s_deleted, @"relative\a")));
        Assert.Null(RecycleBinInfoFile.TryParse(RecycleBinInfoFile.CreateV2(1, s_deleted, string.Empty)));

        var data = RecycleBinInfoFile.CreateV2(1, s_deleted, @"C:\a");
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), -5);
        Assert.Null(RecycleBinInfoFile.TryParse(data));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), int.MaxValue);
        Assert.Null(RecycleBinInfoFile.TryParse(data));
    }
}
