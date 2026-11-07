using DotDeck.Core.FileOperations;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.FileOperations;

public sealed class FileOperationServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeRecycleBin _bin;
    private readonly FileOperationService _service;

    public FileOperationServiceTests()
    {
        Directory.CreateDirectory(_temp.Combine("w"));
        _bin = new FakeRecycleBin(_temp.Combine("bin"));
        _service = new FileOperationService(PhysicalFileStore.Instance, _bin, new FileOperationHistory());
    }

    private string W(params string[] parts) => _temp.Combine(["w", .. parts]);

    public void Dispose() => _temp.Dispose();

    private void Write(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Create_file_and_folder_then_undo_sends_them_to_the_recycle_bin()
    {
        _service.CreateFile(W(), "a.md");
        _service.CreateFolder(W(), "sub");

        Assert.True(File.Exists(W("a.md")));
        Assert.True(Directory.Exists(W("sub")));

        _service.Undo();
        _service.Undo();

        Assert.False(File.Exists(W("a.md")));
        Assert.False(Directory.Exists(W("sub")));
        Assert.Equal(2, _bin.RecycleCount);

        // Redo restores the same items rather than recreating empty ones.
        _service.Redo();
        Assert.True(File.Exists(W("a.md")));
    }

    [Fact]
    public void Create_refuses_existing_name_and_never_truncates()
    {
        Write(W("a.md"), "keep");

        var ex = Assert.Throws<FileOperationException>(() => _service.CreateFile(W(), "A.md"));

        Assert.Contains("이미", ex.Message, StringComparison.Ordinal);
        Assert.Equal("keep", File.ReadAllText(W("a.md")));
        Assert.False(_service.History.CanUndo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("what?")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("Com1.md")]
    [InlineData("LPT9")]
    [InlineData("..")]
    public void Invalid_names_are_rejected(string name)
    {
        Assert.NotNull(FileNameRules.Validate(name));
        Assert.Throws<FileOperationException>(() => _service.CreateFile(W(), name));
    }

    [Theory]
    [InlineData("a.md")]
    [InlineData(".env.example")]
    [InlineData("CONFIG.md")]
    [InlineData("com10")]
    [InlineData("한글 이름.txt")]
    public void Valid_names_are_accepted(string name)
    {
        Assert.Null(FileNameRules.Validate(name));
    }

    [Fact]
    public void Rename_and_undo_redo()
    {
        Write(W("a.md"), "content");

        _service.Rename(W("a.md"), "b.md");
        Assert.Equal("content", File.ReadAllText(W("b.md")));
        Assert.Contains("이름 바꾸기", _service.History.UndoDescription, StringComparison.Ordinal);

        _service.Undo();
        Assert.True(File.Exists(W("a.md")));
        Assert.False(File.Exists(W("b.md")));

        _service.Redo();
        Assert.True(File.Exists(W("b.md")));
    }

    [Fact]
    public void Case_only_rename_works_for_files_and_folders()
    {
        Write(W("readme.md"));
        Directory.CreateDirectory(W("docs"));

        _service.Rename(W("readme.md"), "README.md");
        _service.Rename(W("docs"), "Docs");

        var names = Directory.GetFileSystemEntries(W()).Select(Path.GetFileName).ToList();
        Assert.Contains("README.md", names);
        Assert.Contains("Docs", names);

        _service.Undo();
        Assert.Contains("docs", Directory.GetFileSystemEntries(W()).Select(Path.GetFileName));
    }

    [Fact]
    public void Rename_never_overwrites()
    {
        Write(W("a.md"), "a");
        Write(W("b.md"), "b");

        Assert.Throws<FileOperationException>(() => _service.Rename(W("a.md"), "b.md"));

        Assert.Equal("a", File.ReadAllText(W("a.md")));
        Assert.Equal("b", File.ReadAllText(W("b.md")));
    }

    [Fact]
    public void Undo_refuses_when_a_new_item_took_the_old_place()
    {
        Write(W("a.md"), "original");
        _service.Rename(W("a.md"), "b.md");
        Write(W("a.md"), "newcomer");

        var ex = Assert.Throws<FileOperationException>(() => _service.Undo());

        Assert.Contains("이미", ex.Message, StringComparison.Ordinal);
        Assert.Equal("newcomer", File.ReadAllText(W("a.md")));
        Assert.Equal("original", File.ReadAllText(W("b.md")));

        // The entry stays so the user can retry after resolving the conflict.
        Assert.True(_service.History.CanUndo);
        File.Delete(W("a.md"));
        _service.Undo();
        Assert.Equal("original", File.ReadAllText(W("a.md")));
    }

    [Fact]
    public void Move_several_items_and_undo_as_one_entry()
    {
        Write(W("a.md"));
        Write(W("dir", "inner.md"));
        Directory.CreateDirectory(W("target"));

        var result = _service.Move([W("a.md"), W("dir"), W("dir", "inner.md")], W("target"));

        Assert.Equal(2, result.Paths.Count);
        Assert.True(File.Exists(W("target", "a.md")));
        Assert.True(File.Exists(W("target", "dir", "inner.md")));
        Assert.Equal(1, _service.History.UndoCount);

        _service.Undo();
        Assert.True(File.Exists(W("a.md")));
        Assert.True(File.Exists(W("dir", "inner.md")));
        Assert.False(Directory.EnumerateFileSystemEntries(W("target")).Any());
    }

    [Fact]
    public void Move_refuses_conflicts_without_moving_anything()
    {
        Write(W("a.md"), "a");
        Write(W("b.md"), "b");
        Write(W("target", "b.md"), "existing");

        Assert.Throws<FileOperationException>(() => _service.Move([W("a.md"), W("b.md")], W("target")));

        Assert.True(File.Exists(W("a.md")));
        Assert.Equal("existing", File.ReadAllText(W("target", "b.md")));
        Assert.False(_service.History.CanUndo);
    }

    [Fact]
    public void Move_of_two_items_with_the_same_name_is_refused_up_front()
    {
        Write(W("x", "a.md"), "x");
        Write(W("y", "a.md"), "y");
        Directory.CreateDirectory(W("target"));

        Assert.Throws<FileOperationException>(() => _service.Move([W("x", "a.md"), W("y", "a.md")], W("target")));

        Assert.True(File.Exists(W("x", "a.md")));
        Assert.True(File.Exists(W("y", "a.md")));
        Assert.Empty(Directory.GetFiles(W("target")));
    }

    [Fact]
    public void Copy_names_stay_within_the_name_limit()
    {
        var longStem = new string('가', 250);
        var name = FileNameRules.MakeCopyName(longStem + ".md", isDirectory: false, _ => false);
        Assert.True(name.Length <= FileNameRules.MaxNameLength);
        Assert.EndsWith(FileNameRules.CopySuffix + ".md", name, StringComparison.Ordinal);

        var second = FileNameRules.MakeCopyName(new string('a', 255), isDirectory: true, n => n == name || n.EndsWith(FileNameRules.CopySuffix, StringComparison.Ordinal));
        Assert.True(second.Length <= FileNameRules.MaxNameLength);
        Assert.EndsWith(" (2)", second, StringComparison.Ordinal);
        Assert.Null(FileNameRules.Validate(second));

        // An extension that leaves no room is dropped, never cut into an invalid ending.
        var spaced = "a." + new string(' ', 248) + "x";
        Assert.Null(FileNameRules.Validate(FileNameRules.MakeCopyName(spaced, isDirectory: false, _ => false)));
        var emojiExt = "a." + string.Concat(Enumerable.Repeat("😀", 125));
        Assert.Null(FileNameRules.Validate(FileNameRules.MakeCopyName(emojiExt, isDirectory: false, _ => false)));

        // A surrogate pair at the cut is never split.
        var emoji = string.Concat(Enumerable.Repeat("😀", 127)); // 254 UTF-16 units
        var cut = FileNameRules.MakeCopyName(emoji, isDirectory: true, _ => false);
        Assert.False(char.IsHighSurrogate(cut[cut.Length - FileNameRules.CopySuffix.Length - 1]));
    }

    [Fact]
    public void Move_into_itself_is_refused()
    {
        Directory.CreateDirectory(W("dir", "child"));

        Assert.Throws<FileOperationException>(() => _service.Move([W("dir")], W("dir", "child")));
        Assert.Throws<FileOperationException>(() => _service.Move([W("dir")], W("dir")));
        Assert.True(Directory.Exists(W("dir", "child")));
    }

    [Fact]
    public void Move_to_the_same_folder_does_nothing()
    {
        Write(W("a.md"));

        var result = _service.Move([W("a.md")], W());

        Assert.Empty(result.Paths);
        Assert.False(_service.History.CanUndo);
    }

    [Fact]
    public void Copy_uses_copy_names_and_undo_recycles_the_copies()
    {
        Write(W("a.md"), "a");
        Write(W("target", "a.md"), "existing");

        _service.Copy([W("a.md")], W("target"));
        _service.Copy([W("a.md")], W("target"));

        Assert.Equal("existing", File.ReadAllText(W("target", "a.md")));
        Assert.Equal("a", File.ReadAllText(W("target", "a - 복사본.md")));
        Assert.Equal("a", File.ReadAllText(W("target", "a - 복사본 (2).md")));

        _service.Undo();
        Assert.False(File.Exists(W("target", "a - 복사본 (2).md")));
        Assert.True(File.Exists(W("a.md")));
    }

    [Fact]
    public void Duplicate_handles_folders_and_dotfiles()
    {
        Write(W("dir", "x.md"));
        Write(W(".gitignore"));

        _service.Duplicate([W("dir"), W(".gitignore")]);

        Assert.True(File.Exists(W("dir - 복사본", "x.md")));
        Assert.True(File.Exists(W(".gitignore - 복사본")));
    }

    [Fact]
    public void Partial_folder_copy_created_by_the_store_is_undoable()
    {
        Write(W("dir", "x.md"));
        var store = new CopyFailingStore((_, destination) =>
        {
            Directory.CreateDirectory(destination);
            throw new PartialCopyException(destination, new IOException("disk full"));
        });
        var service = new FileOperationService(store, _bin, new FileOperationHistory());

        var ex = Assert.Throws<FileOperationException>(() => service.Duplicate([W("dir")]));

        Assert.True(ex.PartiallyCompleted);
        service.Undo();
        Assert.False(Directory.Exists(W("dir - 복사본")));
    }

    [Fact]
    public void Destination_that_appeared_meanwhile_is_never_claimed()
    {
        Write(W("dir", "x.md"));
        var store = new CopyFailingStore((_, destination) =>
        {
            Directory.CreateDirectory(destination); // another program wins the race
            throw new DestinationExistsException(destination);
        });
        var service = new FileOperationService(store, _bin, new FileOperationHistory());

        var ex = Assert.Throws<FileOperationException>(() => service.Duplicate([W("dir")]));

        Assert.False(ex.PartiallyCompleted);
        Assert.False(service.History.CanUndo);
        Assert.True(Directory.Exists(W("dir - 복사본")));
    }

    [Fact]
    public void Copy_into_itself_is_refused()
    {
        Directory.CreateDirectory(W("dir", "child"));

        Assert.Throws<FileOperationException>(() => _service.Copy([W("dir")], W("dir", "child")));
        Assert.Single(Directory.GetDirectories(W("dir")));
    }

    [Fact]
    public void Delete_recycles_and_undo_restores()
    {
        Write(W("a.md"), "a");
        Write(W("dir", "inner.md"), "inner");

        _service.Delete([W("a.md"), W("dir")]);

        Assert.False(File.Exists(W("a.md")));
        Assert.False(Directory.Exists(W("dir")));

        _service.Undo();
        Assert.Equal("a", File.ReadAllText(W("a.md")));
        Assert.Equal("inner", File.ReadAllText(W("dir", "inner.md")));

        _service.Redo();
        Assert.False(File.Exists(W("a.md")));
        _service.Undo();
        Assert.True(File.Exists(W("a.md")));
    }

    [Fact]
    public void Delete_without_recycle_bin_is_refused_as_a_whole()
    {
        Write(W("a.md"));
        Write(W("net", "b.md"));
        _bin.NoRecycleBinFolders.Add(W("net"));

        Assert.False(_service.CanRecycleAll([W("a.md"), W("net", "b.md")]));
        var ex = Assert.Throws<FileOperationException>(() => _service.Delete([W("a.md"), W("net", "b.md")]));

        Assert.Contains("영구 삭제", ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(W("a.md")));
        Assert.True(File.Exists(W("net", "b.md")));
        Assert.Equal(0, _bin.RecycleCount);
    }

    [Fact]
    public void Undo_of_create_is_refused_where_it_would_delete_permanently()
    {
        Directory.CreateDirectory(W("net"));
        _service.CreateFile(W("net"), "a.md");
        _bin.NoRecycleBinFolders.Add(W("net"));

        Assert.Throws<FileOperationException>(() => _service.Undo());
        Assert.True(File.Exists(W("net", "a.md")));
    }

    [Fact]
    public void Undo_delete_reports_when_the_recycle_bin_was_emptied()
    {
        Write(W("a.md"));
        _service.Delete([W("a.md")]);
        _bin.Empty();

        var ex = Assert.Throws<FileOperationException>(() => _service.Undo());

        Assert.Contains("휴지통에 없습니다", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_warns_when_the_item_cannot_be_restored_later()
    {
        Write(W("a.md"));
        _bin.LoseTrack = true;

        var result = _service.Delete([W("a.md")]);

        Assert.Single(result.Warnings);
        var ex = Assert.Throws<FileOperationException>(() => _service.Undo());
        Assert.Contains("Windows 휴지통", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_refuses_to_overwrite_a_new_item()
    {
        Write(W("a.md"), "old");
        _service.Delete([W("a.md")]);
        Write(W("a.md"), "new");

        Assert.Throws<FileOperationException>(() => _service.Undo());
        Assert.Equal("new", File.ReadAllText(W("a.md")));
    }

    [Fact]
    public void New_operation_clears_redo()
    {
        Write(W("a.md"));
        _service.Rename(W("a.md"), "b.md");
        _service.Undo();
        Assert.True(_service.History.CanRedo);

        _service.CreateFolder(W(), "x");

        Assert.False(_service.History.CanRedo);
        Assert.Null(_service.Redo());
    }

    [Fact]
    public void History_keeps_at_most_its_capacity()
    {
        var history = new FileOperationHistory(capacity: 3);
        var service = new FileOperationService(PhysicalFileStore.Instance, _bin, history);
        for (var i = 0; i < 5; i++)
        {
            service.CreateFolder(W(), $"d{i}");
        }

        Assert.Equal(3, history.UndoCount);
        Assert.Contains("d4", history.UndoDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void Undo_preflight_refuses_the_whole_entry()
    {
        Write(W("a.md"));
        Write(W("b.md"));
        Directory.CreateDirectory(W("target"));
        _service.Move([W("a.md"), W("b.md")], W("target"));

        // Block undo of b only: something new appeared at b's old place.
        Write(W("b.md"), "newcomer");
        Assert.Throws<FileOperationException>(() => _service.Undo());

        // Preflight refused everything, so a is still moved too.
        Assert.True(File.Exists(W("target", "a.md")));
        Assert.True(_service.History.CanUndo);
    }

    /// <summary>Real store whose Copy is replaced by a test action.</summary>
    private sealed class CopyFailingStore(Action<string, string> copy) : IFileStore
    {
        private readonly PhysicalFileStore _inner = PhysicalFileStore.Instance;

        public void Copy(string source, string destination) => copy(source, destination);

        public bool FileExists(string path) => _inner.FileExists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public FileSystemEntry? GetEntry(string path) => _inner.GetEntry(path);

        public IReadOnlyList<FileSystemEntry> GetChildren(string directory) => _inner.GetChildren(directory);

        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);

        public void WriteAllBytes(string path, byte[] bytes) => _inner.WriteAllBytes(path, bytes);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public void CreateFile(string path) => _inner.CreateFile(path);

        public void Move(string source, string destination, bool overwrite = false) => _inner.Move(source, destination, overwrite);

        public void DeleteFile(string path) => _inner.DeleteFile(path);

        public IDisposable AcquireLock(string lockPath, TimeSpan timeout) => _inner.AcquireLock(lockPath, timeout);
    }

    [Fact]
    public void Changed_is_raised_for_record_undo_and_redo()
    {
        var count = 0;
        _service.History.Changed += (_, _) => count++;

        _service.CreateFolder(W(), "d");
        _service.Undo();
        _service.Redo();

        Assert.Equal(3, count);
    }
}
