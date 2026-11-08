using DotDeck.Core.Documents;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Documents;

public sealed class BackupStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 10, 20, 30, TimeSpan.Zero));
    private readonly BackupStore _store;

    public BackupStoreTests() => _store = new BackupStore(PhysicalFileStore.Instance, _temp.Combine("backups"), _time, retention: 3);

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(@"C:\Users\me\.claude\a.md", @"C\Users\me\.claude\a.md")]
    [InlineData(@"d:\x\y.txt", @"D\x\y.txt")]
    [InlineData(@"\\server\share\dir\f.json", @"UNC\server\share\dir\f.json")]
    public void Paths_are_mapped_under_drive_or_unc(string original, string expected)
    {
        Assert.Equal(expected, BackupStore.MapToRelativePath(original));
    }

    [Fact]
    public void Create_writes_content_and_manifest_under_timestamp_folder()
    {
        var set = _store.Create([new BackupSource(@"C:\a\b.md", "x"u8.ToArray())], "save");

        Assert.Equal("20261107-102030", set.Id);
        Assert.True(File.Exists(Path.Combine(set.Directory, BackupStore.ManifestFileName)));
        Assert.Equal("x"u8.ToArray(), File.ReadAllBytes(Path.Combine(set.Directory, @"C\a\b.md")));
        var listed = Assert.Single(_store.List());
        Assert.Equal(set.Files, listed.Files);
        Assert.Equal("save", listed.Reason);
    }

    [Fact]
    public void Same_second_backups_get_numbered_folders_sorted_numerically()
    {
        var ids = Enumerable.Range(0, 3)
            .Select(_ => _store.Create([new BackupSource(@"C:\a.md", "x"u8.ToArray())], "save").Id)
            .ToList();

        Assert.Equal(["20261107-102030", "20261107-102030-2", "20261107-102030-3"], ids);
        Assert.Equal(["20261107-102030-3", "20261107-102030-2", "20261107-102030"], _store.List().Select(s => s.Id));
    }

    [Fact]
    public void Old_sets_beyond_retention_are_pruned()
    {
        for (var i = 0; i < 5; i++)
        {
            _store.Create([new BackupSource(@"C:\a.md", [(byte)i])], "save");
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        var sets = _store.List();
        Assert.Equal(3, sets.Count);
        Assert.Equal("20261107-102430", sets[0].Id);
        Assert.Equal(3, Directory.GetDirectories(_temp.Combine("backups")).Length);
    }

    [Fact]
    public void Recent_sets_are_kept_even_beyond_retention()
    {
        for (var i = 0; i < 5; i++)
        {
            _store.Create([new BackupSource(@"C:\a.md", [(byte)i])], "save");
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(5, _store.List().Count);
    }

    [Fact]
    public void Damaged_backup_content_is_detected()
    {
        var set = _store.Create([new BackupSource(@"C:\a.md", "x"u8.ToArray())], "save");
        File.WriteAllBytes(Path.Combine(set.Directory, set.Files[0].RelativePath), "tampered"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => _store.ReadContent(set, set.Files[0]));
    }

    [Fact]
    public void Manifest_escaping_the_set_folder_is_ignored()
    {
        var set = _store.Create([new BackupSource(@"C:\a.md", "x"u8.ToArray())], "save");
        var manifest = Path.Combine(set.Directory, BackupStore.ManifestFileName);
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(@"C\\a.md", @"..\\..\\evil.txt", StringComparison.Ordinal));

        Assert.Empty(_store.List());
    }

    [Fact]
    public void Folders_without_manifest_are_skipped()
    {
        Directory.CreateDirectory(_temp.Combine("backups", "junk"));
        _store.Create([new BackupSource(@"C:\a.md", "x"u8.ToArray())], "save");

        Assert.Single(_store.List());
    }

    [Fact]
    public void Secret_files_are_never_backed_up()
    {
        Assert.Throws<SecretFileException>(() => _store.Create([new BackupSource(@"C:\p\.env", "K=V"u8.ToArray())], "save"));
        Assert.Empty(_store.List());
    }

    [Fact]
    public void List_of_missing_backups_folder_is_empty()
    {
        Assert.Empty(_store.List());
    }
}
