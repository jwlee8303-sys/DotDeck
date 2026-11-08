using DotDeck.Core.Documents;
using DotDeck.Core.FileOperations;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.FileOperations;

public sealed class PasteTargetResolverTests
{
    [Fact]
    public void Folder_pastes_inside_file_pastes_next_to_it_and_no_selection_is_an_error()
    {
        Assert.Equal(@"D:\p\docs", PasteTargetResolver.Resolve(new PasteSelection(PasteSelectionKind.Folder, @"D:\p\docs")));
        Assert.Equal(@"D:\p\docs", PasteTargetResolver.Resolve(new PasteSelection(PasteSelectionKind.File, @"D:\p\docs\a.md")));
        Assert.Equal(@"D:\p", PasteTargetResolver.Resolve(new PasteSelection(PasteSelectionKind.ProjectRoot, @"D:\p")));

        var error = Assert.Throws<FileOperationException>(() => PasteTargetResolver.Resolve(new PasteSelection(PasteSelectionKind.None, null)));
        Assert.Equal(PasteTargetResolver.NoSelectionMessage, error.Message);
    }

    [Fact]
    public void Merged_compact_row_uses_the_last_folder_unless_a_segment_was_clicked()
    {
        IReadOnlyList<string> chain = [@"D:\p\src", @"D:\p\src\App", @"D:\p\src\App\Themes"];

        Assert.Equal(@"D:\p\src\App\Themes", PasteTargetResolver.Resolve(new PasteSelection(PasteSelectionKind.Folder, chain[^1], chain)));
        Assert.Equal(@"D:\p\src\App", PasteTargetResolver.Resolve(new PasteSelection(PasteSelectionKind.Folder, chain[^1], chain, ActiveSegment: 1)));
    }

    [Fact]
    public void Flat_view_file_uses_its_real_folder()
    {
        // A flat row shows "a.md  docs\deep" at the top level; the selection carries its real path.
        Assert.Equal(@"D:\p\docs\deep", PasteTargetResolver.Resolve(new PasteSelection(PasteSelectionKind.File, @"D:\p\docs\deep\a.md")));
    }
}

public sealed class PasteTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeRecycleBin _bin;
    private readonly BackupStore _backups;
    private readonly FileOperationService _service;

    public PasteTests()
    {
        Directory.CreateDirectory(W());
        _bin = new FakeRecycleBin(_temp.Combine("bin"));
        _backups = new BackupStore(PhysicalFileStore.Instance, _temp.Combine("backups"), TimeProvider.System);
        _service = new FileOperationService(PhysicalFileStore.Instance, _bin, new FileOperationHistory(), _backups);
    }

    public void Dispose() => _temp.Dispose();

    private string W(params string[] parts) => _temp.Combine(["w", .. parts]);

    private static void Write(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static readonly Dictionary<string, ConflictChoice> s_none = [];

    [Fact]
    public void Duplicate_names_copies_in_the_same_folder()
    {
        Write(W("a.md"));

        _service.Duplicate([W("a.md")]);
        var second = _service.Duplicate([W("a.md")]);

        Assert.True(File.Exists(W("a - 복사본.md")));
        Assert.Equal([W("a - 복사본 (2).md")], second.Paths);
        Assert.Equal([new PathChange.Created(W("a - 복사본 (2).md"))], second.Changes);
    }

    [Fact]
    public void Pasting_into_the_same_folder_duplicates()
    {
        Write(W("a.md"));

        var plan = _service.PlanPaste([W("a.md")], W(), isMove: false);
        var result = _service.Paste(plan, s_none);

        Assert.Equal(PasteItemKind.SameFolder, Assert.Single(plan.Items).Kind);
        Assert.Equal([W("a - 복사본.md")], result.Paths);
    }

    [Fact]
    public void A_folder_cannot_be_pasted_into_itself_or_below_it()
    {
        Write(W("f", "sub", "a.md"));

        Assert.Throws<FileOperationException>(() => _service.PlanPaste([W("f")], W("f", "sub"), isMove: false));
        Assert.Throws<FileOperationException>(() => _service.PlanPaste([W("f")], W("f"), isMove: true));
        Assert.False(Directory.Exists(W("f", "sub", "f")));
    }

    [Fact]
    public void Conflicts_offer_overwrite_keep_both_and_skip()
    {
        Write(W("src", "a.md"), "new");
        Write(W("src", "b.md"), "b");
        Write(W("src", "c.md"), "c");
        Write(W("dst", "a.md"), "old");
        Write(W("dst", "b.md"), "old b");
        Write(W("dst", "c.md"), "old c");
        var plan = _service.PlanPaste([W("src", "a.md"), W("src", "b.md"), W("src", "c.md")], W("dst"), isMove: false);
        Assert.All(plan.Items, i => Assert.Equal(PasteItemKind.Conflict, i.Kind));
        Assert.All(plan.Items, i => Assert.True(i.CanOverwrite));

        var result = _service.Paste(plan, new Dictionary<string, ConflictChoice>
        {
            [W("src", "a.md")] = ConflictChoice.Overwrite,
            [W("src", "b.md")] = ConflictChoice.KeepBoth,
            [W("src", "c.md")] = ConflictChoice.Skip,
        });

        Assert.Equal("new", File.ReadAllText(W("dst", "a.md")));
        Assert.Equal("b", File.ReadAllText(W("dst", "b - 복사본.md")));
        Assert.Equal("old b", File.ReadAllText(W("dst", "b.md")));
        Assert.Equal("old c", File.ReadAllText(W("dst", "c.md")));
        Assert.Equal([W("dst", "a.md"), W("dst", "b - 복사본.md")], result.Paths);

        // SPEC 6: the overwritten file was backed up first.
        var set = Assert.Single(_backups.List());
        Assert.Equal("old", System.Text.Encoding.UTF8.GetString(_backups.ReadContent(set, Assert.Single(set.Files))));
    }

    [Fact]
    public void Undo_of_an_overwriting_paste_restores_the_old_file_and_removes_the_copies()
    {
        Write(W("src", "a.md"), "new");
        Write(W("src", "n.md"), "n");
        Write(W("dst", "a.md"), "old");
        var plan = _service.PlanPaste([W("src", "a.md"), W("src", "n.md")], W("dst"), isMove: false);
        _service.Paste(plan, new Dictionary<string, ConflictChoice> { [W("src", "a.md")] = ConflictChoice.Overwrite });

        var undo = _service.Undo()!;

        Assert.Equal("old", File.ReadAllText(W("dst", "a.md")));
        Assert.False(File.Exists(W("dst", "n.md")));
        Assert.Contains(new PathChange.Created(W("dst", "a.md")), undo.Changes);
        Assert.Contains(new PathChange.Removed(W("dst", "n.md")), undo.Changes);

        _service.Redo();
        Assert.Equal("new", File.ReadAllText(W("dst", "a.md")));
        Assert.True(File.Exists(W("dst", "n.md")));
    }

    [Fact]
    public void Cut_and_paste_moves_and_undo_moves_back_reporting_both_directions()
    {
        Write(W("a", "x.md"));
        Directory.CreateDirectory(W("b"));

        var result = _service.Paste(_service.PlanPaste([W("a", "x.md")], W("b"), isMove: true), s_none);
        var undo = _service.Undo()!;

        Assert.Equal([new PathChange.Moved(W("a", "x.md"), W("b", "x.md"))], result.Changes);
        Assert.Equal([new PathChange.Moved(W("b", "x.md"), W("a", "x.md"))], undo.Changes);
        Assert.True(File.Exists(W("a", "x.md")));
    }

    [Fact]
    public void Folders_cannot_be_overwritten()
    {
        Write(W("src", "d", "a.md"));
        Write(W("dst", "d", "b.md"));
        var plan = _service.PlanPaste([W("src", "d")], W("dst"), isMove: false);

        Assert.False(Assert.Single(plan.Items).CanOverwrite);
        Assert.Throws<FileOperationException>(() => _service.Paste(plan, new Dictionary<string, ConflictChoice> { [W("src", "d")] = ConflictChoice.Overwrite }));
        Assert.True(File.Exists(W("dst", "d", "b.md")));
    }

    [Fact]
    public void A_conflict_that_appears_after_planning_is_refused()
    {
        Write(W("src", "a.md"));
        Directory.CreateDirectory(W("dst"));
        var plan = _service.PlanPaste([W("src", "a.md")], W("dst"), isMove: false);
        Write(W("dst", "a.md"), "someone else");

        Assert.Throws<FileOperationException>(() => _service.Paste(plan, s_none));
        Assert.Equal("someone else", File.ReadAllText(W("dst", "a.md")));
    }

    [Fact]
    public void A_source_that_became_a_folder_after_planning_is_refused()
    {
        Write(W("src", "a"), "file");
        Write(W("dst", "a"), "old");
        var plan = _service.PlanPaste([W("src", "a")], W("dst"), isMove: false);
        File.Delete(W("src", "a"));
        Write(W("src", "a", "inner.md"));

        Assert.Throws<FileOperationException>(() => _service.Paste(plan, new Dictionary<string, ConflictChoice> { [W("src", "a")] = ConflictChoice.Overwrite }));
        Assert.Equal("old", File.ReadAllText(W("dst", "a")));
        Assert.Equal(0, _bin.RecycleCount);
    }

    [Fact]
    public void Rename_delete_and_restore_report_path_changes()
    {
        Write(W("a.md"));

        var rename = _service.Rename(W("a.md"), "b.md");
        var delete = _service.Delete([W("b.md")]);
        var restore = _service.Undo()!;

        Assert.Equal([new PathChange.Moved(W("a.md"), W("b.md"))], rename.Changes);
        Assert.Equal([new PathChange.Removed(W("b.md"))], delete.Changes);
        Assert.Equal([new PathChange.Created(W("b.md"))], restore.Changes);
    }
}
