using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using DotDeck.Core.Logging;
using DotDeck.Core.Projects;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Explorer;

public sealed class ExplorerTreeBuilderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private FolderProjectSource Source() => new(
        new ProjectDefinition { Id = "p1", Kind = ProjectKind.Folder, Name = "P", Root = _temp.Path },
        _temp.Path,
        PhysicalFileStore.Instance);

    private void Touch(params string[] parts)
    {
        var path = _temp.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private static HashSet<string> Keys(params string[] keys) => new(keys, ExplorerTreeBuilder.KeyComparer);

    private static string Describe(TreeRow row) => $"{new string(' ', row.Depth)}{row.Node.Name}{(row.IsExpanded ? "/" : "")}";

    [Fact]
    public void Collapsed_tree_lists_only_root_children()
    {
        Touch("docs", "a.md");
        Touch("README.md");

        var result = ExplorerTreeBuilder.Build(Source(), Keys(), TestContext.Current.CancellationToken);

        Assert.Equal(ProjectRootState.Ready, result.RootState);
        Assert.Equal(["docs", "README.md"], result.Rows.Select(Describe));
    }

    [Fact]
    public void Expanded_keys_are_restored_recursively_and_case_insensitively()
    {
        Touch("docs", "guide", "x.md");
        Touch("docs", "a.md");
        Touch("src", "b.cs");

        var result = ExplorerTreeBuilder.Build(Source(), Keys("DOCS", @"docs\guide"), TestContext.Current.CancellationToken);

        Assert.Equal(["docs/", " guide/", "  x.md", " a.md", "src"], result.Rows.Select(Describe));
        Assert.Empty(result.StaleExpandedKeys);
    }

    [Fact]
    public void Expanded_child_under_collapsed_parent_is_kept_but_not_shown()
    {
        Touch("docs", "guide", "x.md");

        var result = ExplorerTreeBuilder.Build(Source(), Keys(@"docs\guide"), TestContext.Current.CancellationToken);

        Assert.Equal(["docs"], result.Rows.Select(Describe));
        Assert.Empty(result.StaleExpandedKeys);
    }

    [Fact]
    public void Keys_of_deleted_folders_are_reported_stale()
    {
        Touch("docs", "a.md");

        var result = ExplorerTreeBuilder.Build(Source(), Keys("docs", "gone", @"docs\old"), TestContext.Current.CancellationToken);

        Assert.Equal(Keys("gone", @"docs\old"), result.StaleExpandedKeys);
    }

    [Fact]
    public void Descendant_keys_of_deleted_folders_are_stale_too()
    {
        Touch("docs", "a.md");
        Touch("gonermost", "b.md");

        // "gonermost" exists and only shares a string prefix with "gone": it must not be swept up.
        var result = ExplorerTreeBuilder.Build(
            Source(), Keys("gone", @"gone\child", @"GONE\child\deeper", "gonermost", "docs"), TestContext.Current.CancellationToken);

        Assert.Equal(Keys("gone", @"gone\child", @"GONE\child\deeper"), result.StaleExpandedKeys);
    }

    [Fact]
    public void Missing_root_has_no_rows()
    {
        var root = _temp.Combine("missing");
        var source = new FolderProjectSource(
            new ProjectDefinition { Id = "p", Kind = ProjectKind.Folder, Name = "P", Root = root }, root, PhysicalFileStore.Instance);

        var result = ExplorerTreeBuilder.Build(source, Keys("x"), TestContext.Current.CancellationToken);

        Assert.Equal(ProjectRootState.Missing, result.RootState);
        Assert.Empty(result.Rows);
        Assert.Empty(result.StaleExpandedKeys);
    }

    [Fact]
    public void Listing_errors_are_attached_to_the_folder_row()
    {
        var store = new FailingStore(_temp.Combine("locked"));
        Touch("locked", "a.md");
        Touch("ok", "b.md");
        var source = new FolderProjectSource(
            new ProjectDefinition { Id = "p", Kind = ProjectKind.Folder, Name = "P", Root = _temp.Path }, _temp.Path, store);

        var result = ExplorerTreeBuilder.Build(source, Keys("locked", "ok"), TestContext.Current.CancellationToken);

        Assert.Equal(["locked", "ok/", " b.md"], result.Rows.Select(Describe));
        Assert.NotNull(result.Rows[0].Error);
        Assert.Throws<UnauthorizedAccessException>(() =>
            ExplorerTreeBuilder.BuildChildren(source, result.Rows[0].Node, 0, Keys(), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Root_listing_error_is_reported()
    {
        var store = new FailingStore(_temp.Path);

        var result = ExplorerTreeBuilder.Build(
            new FolderProjectSource(new ProjectDefinition { Id = "p", Kind = ProjectKind.Folder, Name = "P", Root = _temp.Path }, _temp.Path, store),
            Keys(), TestContext.Current.CancellationToken);

        Assert.NotNull(result.RootError);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void BuildChildren_returns_rows_below_parent_with_nested_expansion()
    {
        Touch("docs", "guide", "x.md");
        Touch("docs", "a.md");
        var source = Source();
        var docs = ExplorerTreeBuilder.Build(source, Keys(), TestContext.Current.CancellationToken).Rows.Single().Node;

        var rows = ExplorerTreeBuilder.BuildChildren(
            source, docs, 0, Keys(@"docs\guide"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([" guide/", "  x.md", " a.md"], rows.Select(Describe));
    }

    [Fact]
    public void Cancellation_stops_the_build()
    {
        Touch("docs", "a.md");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => ExplorerTreeBuilder.Build(Source(), Keys("docs"), cts.Token));
    }

    [Fact]
    public void Session_state_round_trips_and_preserves_other_sections()
    {
        var file = _temp.Combine("state", "session.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{ "schemaVersion": 1, "documents": [1, 2] }""");
        var store = new SessionStateStore(PhysicalFileStore.Instance, file, new ListAppLog());

        store.SaveExplorers(new Dictionary<string, ExplorerViewState>
        {
            ["p1"] = new(true, ["docs", @"docs\guide"], @"docs\a.md", IsDetached: true),
            ["builtin.claude"] = ExplorerViewState.Default,
        });

        var loaded = store.LoadExplorers();
        Assert.True(loaded["p1"].IsCollapsed);
        Assert.Equal(["docs", @"docs\guide"], loaded["p1"].Expanded);
        Assert.Equal(@"docs\a.md", loaded["p1"].Selected);
        Assert.True(loaded["p1"].IsDetached);
        Assert.False(loaded["builtin.claude"].IsDetached);
        Assert.False(loaded["builtin.claude"].IsCollapsed);
        Assert.Contains("\"documents\"", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{ "schemaVersion": "x" }""")]
    [InlineData("""{ "explorers": {}, "explorers": {} }""")]
    [InlineData("""{ "explorers": { "p1": { "collapsed": "yes", "expanded": [1, "a"], "selected": 3 } } }""")]
    [InlineData("""{ "explorers": { "p1": { "collapsed": false, "collapsed": true } } }""")]
    [InlineData("""{ "explorers": { "p1": {}, "p1": {} } }""")]
    public void Damaged_session_is_tolerated(string json)
    {
        var file = _temp.Combine("session.json");
        File.WriteAllText(file, json);
        var log = new ListAppLog();
        var store = new SessionStateStore(PhysicalFileStore.Instance, file, log);

        var loaded = store.LoadExplorers();
        store.SaveExplorers(new Dictionary<string, ExplorerViewState> { ["p2"] = ExplorerViewState.Default });

        if (loaded.TryGetValue("p1", out var p1))
        {
            Assert.False(p1.IsCollapsed);
            Assert.Equal(["a"], p1.Expanded);
            Assert.Null(p1.Selected);
        }

        Assert.True(store.LoadExplorers().ContainsKey("p2"));
    }

    [Fact]
    public void Newer_session_schema_is_never_overwritten()
    {
        var file = _temp.Combine("session.json");
        const string Newer = """{ "schemaVersion": 99, "explorers": { "p1": { "collapsed": true } } }""";
        File.WriteAllText(file, Newer);
        var store = new SessionStateStore(PhysicalFileStore.Instance, file, new ListAppLog());

        Assert.Empty(store.LoadExplorers());
        store.SaveExplorers(new Dictionary<string, ExplorerViewState> { ["p2"] = ExplorerViewState.Default });

        Assert.Equal(Newer, File.ReadAllText(file));
    }

    /// <summary>Real file store that refuses to list one directory.</summary>
    private sealed class FailingStore(string deniedDirectory) : IFileStore
    {
        private readonly PhysicalFileStore _inner = PhysicalFileStore.Instance;

        public IReadOnlyList<FileSystemEntry> GetChildren(string directory) =>
            string.Equals(Path.GetFullPath(directory), Path.GetFullPath(deniedDirectory), StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("denied")
                : _inner.GetChildren(directory);

        public bool FileExists(string path) => _inner.FileExists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public FileSystemEntry? GetEntry(string path) => _inner.GetEntry(path);

        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);

        public byte[] ReadHead(string path, int maxBytes) => _inner.ReadHead(path, maxBytes);

        public string? ResolveLinkTarget(string path) => _inner.ResolveLinkTarget(path);

        public void WriteAllBytes(string path, byte[] bytes) => _inner.WriteAllBytes(path, bytes);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public void CreateFile(string path) => _inner.CreateFile(path);

        public void Move(string source, string destination, bool overwrite = false) => _inner.Move(source, destination, overwrite);

        public void Copy(string source, string destination) => _inner.Copy(source, destination);

        public void DeleteFile(string path) => _inner.DeleteFile(path);

        public IDisposable AcquireLock(string lockPath, TimeSpan timeout) => _inner.AcquireLock(lockPath, timeout);
    }
}
