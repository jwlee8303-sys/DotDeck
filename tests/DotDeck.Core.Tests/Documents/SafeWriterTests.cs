using System.Text;
using DotDeck.Core.Documents;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Documents;

public sealed class SafeWriterTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 10, 20, 30, TimeSpan.Zero));
    private readonly BackupStore _backups;
    private readonly SafeWriter _writer;
    private readonly TextFileReader _reader = new(PhysicalFileStore.Instance);

    public SafeWriterTests()
    {
        _backups = new BackupStore(PhysicalFileStore.Instance, _temp.Combine("backups"), _time);
        _writer = new SafeWriter(PhysicalFileStore.Instance, _backups);
        Directory.CreateDirectory(_temp.Combine("work"));
    }

    public void Dispose() => _temp.Dispose();

    private string Work(string name, byte[] bytes)
    {
        var path = _temp.Combine("work", name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Cp949(string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(949).GetBytes(text);
    }

    private string[] TempFiles() => Directory.GetFiles(_temp.Combine("work"), "*.dotdeck.tmp", SearchOption.AllDirectories);

    [Fact]
    public void Saving_unchanged_text_leaves_bytes_and_mtime_untouched()
    {
        // Mixed endings and a non-round-tripping file must still be left alone.
        byte[] original = [0xEF, 0xBB, 0xBF, .. "a\r\nb\nc"u8];
        var path = Work("a.md", original);
        var mtime = File.GetLastWriteTimeUtc(path);
        var snapshot = _reader.Read(path);

        var plan = SafeWriter.Prepare(path, snapshot, snapshot.Text);
        var result = _writer.Commit(plan);

        Assert.True(plan.IsUnchanged);
        Assert.False(result.Written);
        Assert.Null(result.Backup);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));
        Assert.Empty(_backups.List());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Edit_preserves_bom_and_line_endings(bool bom)
    {
        byte[] original = [.. (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : []), .. "x\ny\n"u8];
        var path = Work("a.md", original);
        var snapshot = _reader.Read(path);

        var result = _writer.Commit(SafeWriter.Prepare(path, snapshot, "x\nz\n"));

        byte[] expected = [.. (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : []), .. "x\nz\n"u8];
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.True(result.Written);
        Assert.Equal(TextFileReader.ComputeHash(expected), result.Snapshot.Sha256);
    }

    [Fact]
    public void Cp949_file_is_written_back_in_cp949()
    {
        var path = Work("k.ini", Cp949("이름=값\r\n"));
        var snapshot = _reader.Read(path);

        _writer.Commit(SafeWriter.Prepare(path, snapshot, "이름=새값\r\n"));

        Assert.Equal(Cp949("이름=새값\r\n"), File.ReadAllBytes(path));
    }

    [Fact]
    public void Unencodable_characters_block_the_plan_with_positions()
    {
        var path = Work("k.ini", Cp949("한\r\n"));
        var snapshot = _reader.Read(path);
        Assert.Equal(TextEncodingKind.Cp949, snapshot.Encoding);

        var plan = SafeWriter.Prepare(path, snapshot, "한\r\nb😀");

        Assert.False(plan.CanCommit);
        Assert.Null(plan.NewBytes);
        var bad = Assert.Single(plan.UnencodableCharacters);
        Assert.Equal((2, 2, "😀"), (bad.Line, bad.Column, bad.Text));
        Assert.Throws<InvalidOperationException>(() => _writer.Commit(plan));

        var asUtf8 = SafeWriter.Prepare(path, snapshot, "a\r\nb😀", TextEncodingKind.Utf8);
        Assert.True(asUtf8.CanCommit);
    }

    [Fact]
    public void Decoding_errors_block_saving_unless_encoding_is_chosen()
    {
        var path = Work("bad.txt", [(byte)'a', (byte)'b', 0xB0]);
        var snapshot = _reader.Read(path);

        Assert.False(SafeWriter.Prepare(path, snapshot, "ab!").CanCommit);
        Assert.True(SafeWriter.Prepare(path, snapshot, "ab!", TextEncodingKind.Utf8).CanCommit);
    }

    [Fact]
    public void Save_backs_up_previous_content_first()
    {
        var path = Work("a.md", "old"u8.ToArray());
        var snapshot = _reader.Read(path);

        var result = _writer.Commit(SafeWriter.Prepare(path, snapshot, "new"));

        Assert.NotNull(result.Backup);
        var (set, file) = Assert.Single(_backups.ListFor(path));
        Assert.Equal("old"u8.ToArray(), _backups.ReadContent(set, file));
        Assert.Equal("save", set.Reason);
    }

    [Fact]
    public void External_modification_is_detected_and_nothing_is_written()
    {
        var path = Work("a.md", "v1"u8.ToArray());
        var snapshot = _reader.Read(path);
        File.WriteAllBytes(path, "v2"u8.ToArray());

        var ex = Assert.Throws<ExternalChangeException>(() => _writer.Commit(SafeWriter.Prepare(path, snapshot, "mine")));

        Assert.Equal(ExternalChangeKind.Modified, ex.Kind);
        Assert.Equal("v2"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Empty(_backups.List());
    }

    [Fact]
    public void External_change_can_be_overwritten_and_is_backed_up()
    {
        var path = Work("a.md", "v1"u8.ToArray());
        var snapshot = _reader.Read(path);
        File.WriteAllBytes(path, "v2"u8.ToArray());

        _writer.Commit(SafeWriter.Prepare(path, snapshot, "mine"), overwriteExternalChanges: true);

        Assert.Equal("mine"u8.ToArray(), File.ReadAllBytes(path));
        var (set, file) = Assert.Single(_backups.ListFor(path));
        Assert.Equal("v2"u8.ToArray(), _backups.ReadContent(set, file));
    }

    [Fact]
    public void Deleted_file_is_reported_as_external_change()
    {
        var path = Work("a.md", "v1"u8.ToArray());
        var snapshot = _reader.Read(path);
        File.Delete(path);

        var ex = Assert.Throws<ExternalChangeException>(() => _writer.Commit(SafeWriter.Prepare(path, snapshot, "x")));
        Assert.Equal(ExternalChangeKind.Deleted, ex.Kind);
    }

    [Fact]
    public void New_file_is_created_and_refuses_to_clobber_an_existing_one()
    {
        var path = _temp.Combine("work", "new.md");

        var result = _writer.Commit(SafeWriter.Prepare(path, null, "hi"));
        Assert.Equal("hi"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Null(result.Backup);

        var ex = Assert.Throws<ExternalChangeException>(() => _writer.Commit(SafeWriter.Prepare(path, null, "again")));
        Assert.Equal(ExternalChangeKind.Created, ex.Kind);
    }

    [Fact]
    public void Read_only_file_is_refused()
    {
        var path = Work("ro.md", "a"u8.ToArray());
        var snapshot = _reader.Read(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<ReadOnlyFileException>(() => _writer.Commit(SafeWriter.Prepare(path, snapshot, "b")));
            Assert.Equal("a"u8.ToArray(), File.ReadAllBytes(path));
            Assert.Empty(TempFiles());
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Failure_during_replace_removes_temp_file_and_keeps_original()
    {
        var path = Work("a.md", "old"u8.ToArray());
        var snapshot = _reader.Read(path);
        var writer = new SafeWriter(new FailingReplaceStore(), _backups);

        Assert.Throws<IOException>(() => writer.Commit(SafeWriter.Prepare(path, snapshot, "new")));

        Assert.Equal("old"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public void Hidden_attribute_is_kept_after_save()
    {
        var path = Work("h.md", "a"u8.ToArray());
        File.SetAttributes(path, FileAttributes.Hidden);
        var snapshot = _reader.Read(path);

        _writer.Commit(SafeWriter.Prepare(path, snapshot, "b"));

        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void Text_with_nul_character_saves_and_reports_success()
    {
        var path = Work("n.txt", "a"u8.ToArray());

        var result = _writer.Commit(SafeWriter.Prepare(path, _reader.Read(path), "a b"));

        Assert.True(result.Written);
        Assert.Equal("a b", result.Snapshot.Text);
        Assert.Equal("a b"u8.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void Link_to_secret_file_is_not_saved_or_backed_up()
    {
        var secret = Work(".env", "K=V"u8.ToArray());
        var link = _temp.Combine("work", "notes.txt");
        try
        {
            File.CreateSymbolicLink(link, secret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic links need Developer Mode or elevation.");
        }

        _writer.Commit(SafeWriter.Prepare(link, null, "x"), overwriteExternalChanges: true);
        Assert.Equal("x", File.ReadAllText(secret));
        Assert.Empty(_backups.List());
    }

    [Fact]
    public void Secret_file_cannot_be_saved()
    {
        var plan = SafeWriter.Prepare(_temp.Combine("work", ".env"), null, "K=V");

        Assert.True(plan.CanCommit);
    }

    [Fact]
    public void Plan_retargeted_to_secret_path_is_refused_at_commit()
    {
        var plan = SafeWriter.Prepare(_temp.Combine("work", "ok.txt"), null, "K=V") with { Path = _temp.Combine("work", ".env") };

        _writer.Commit(plan);
        Assert.True(File.Exists(_temp.Combine("work", ".env")));
    }

    [Fact]
    public void Plan_reports_diff_and_line_ending_change()
    {
        var path = Work("a.md", "a\r\nb\r\n"u8.ToArray());
        var snapshot = _reader.Read(path);

        var plan = SafeWriter.Prepare(path, snapshot, "a\nc\n");

        Assert.True(plan.LineEndingsChanged);
        Assert.Equal(1, plan.Diff.InsertedCount);
        Assert.Equal(1, plan.Diff.DeletedCount);
    }

    [Fact]
    public void Restore_writes_backup_back_and_backs_up_current_first()
    {
        var path = Work("a.md", "v1"u8.ToArray());
        _writer.Commit(SafeWriter.Prepare(path, _reader.Read(path), "v2"));
        var (set, file) = Assert.Single(_backups.ListFor(path));
        _time.Advance(TimeSpan.FromSeconds(1));

        var result = _writer.Restore(set, file);

        Assert.Equal("v1"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Equal("before-restore", result.Backup!.Reason);
        Assert.Equal("v2"u8.ToArray(), _backups.ReadContent(result.Backup, result.Backup.Files[0]));
    }

    private sealed class FailingReplaceStore : IFileStore
    {
        private readonly IFileStore _inner = PhysicalFileStore.Instance;

        public void ReplaceFile(string source, string destination) => throw new IOException("replace failed");

        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public FileSystemEntry? GetEntry(string path) => _inner.GetEntry(path);
        public IReadOnlyList<FileSystemEntry> GetChildren(string directory) => _inner.GetChildren(directory);
        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
        public string? ResolveLinkTarget(string path) => _inner.ResolveLinkTarget(path);
        public byte[] ReadHead(string path, int maxBytes) => _inner.ReadHead(path, maxBytes);
        public void WriteAllBytes(string path, byte[] bytes) => _inner.WriteAllBytes(path, bytes);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public void CreateFile(string path) => _inner.CreateFile(path);
        public void Move(string source, string destination, bool overwrite = false) => _inner.Move(source, destination, overwrite);
        public void Copy(string source, string destination) => _inner.Copy(source, destination);
        public void DeleteDirectory(string path) => _inner.DeleteDirectory(path);
        public void DeleteFile(string path) => _inner.DeleteFile(path);
        public IDisposable AcquireLock(string lockPath, TimeSpan timeout) => _inner.AcquireLock(lockPath, timeout);
    }
}
