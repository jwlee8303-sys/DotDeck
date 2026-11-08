using DotDeck.Core.Documents;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Documents;

public sealed class ExternalChangeDetectorTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TextFileReader _reader = new(PhysicalFileStore.Instance);

    public void Dispose() => _temp.Dispose();

    private (string Path, TextFileSnapshot Snapshot) Open(string content)
    {
        var path = _temp.Combine("a.md");
        File.WriteAllText(path, content);
        return (path, _reader.Read(path));
    }

    [Fact]
    public void Untouched_file_is_unchanged()
    {
        var (_, snapshot) = Open("x");

        Assert.Equal(DiskState.Unchanged, ExternalChangeDetector.Check(PhysicalFileStore.Instance, snapshot).State);
    }

    [Fact]
    public void Touch_without_content_change_is_unchanged()
    {
        var (path, snapshot) = Open("x");
        File.SetLastWriteTimeUtc(path, snapshot.LastWriteTimeUtc.AddMinutes(5));

        Assert.Equal(DiskState.Unchanged, ExternalChangeDetector.Check(PhysicalFileStore.Instance, snapshot).State);
    }

    [Fact]
    public void Content_change_is_modified_with_new_hash()
    {
        var (path, snapshot) = Open("x");
        File.WriteAllText(path, "changed");

        var check = ExternalChangeDetector.Check(PhysicalFileStore.Instance, snapshot);

        Assert.Equal(DiskState.Modified, check.State);
        Assert.NotEqual(snapshot.Sha256, check.Sha256);
    }

    [Fact]
    public void Same_size_change_with_preserved_mtime_needs_force()
    {
        var (path, snapshot) = Open("aaaa");
        File.WriteAllText(path, "bbbb");
        File.SetLastWriteTimeUtc(path, snapshot.LastWriteTimeUtc);

        Assert.Equal(DiskState.Unchanged, ExternalChangeDetector.Check(PhysicalFileStore.Instance, snapshot).State);
        Assert.Equal(DiskState.Modified, ExternalChangeDetector.Check(PhysicalFileStore.Instance, snapshot, force: true).State);
    }

    [Fact]
    public void Deleted_file_is_reported()
    {
        var (path, snapshot) = Open("x");
        File.Delete(path);

        var check = ExternalChangeDetector.Check(PhysicalFileStore.Instance, snapshot);

        Assert.Equal(DiskState.Deleted, check.State);
        Assert.Null(check.Sha256);
    }
}
