using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Explorer;

public sealed class SessionDocumentsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Documents_round_trip_and_keep_other_sections()
    {
        var file = _temp.Combine("session.json");
        var store = new SessionStateStore(PhysicalFileStore.Instance, file, new ListAppLog());
        store.SaveExplorers(new Dictionary<string, ExplorerViewState> { ["p1"] = new(true, ["a"], null) });

        store.SaveDocuments(
        [
            new(@"C:\h\.claude\CLAUDE.md", 1, IsPinned: true, IsMarkdownPreviewOpen: true, CaretLine: 12, CaretColumn: 4),
            new(@"C:\h\.claude\CLAUDE.md", 250),
            new(@"C:\h\.codex\config.toml", 1, IsPreview: true),
        ]);

        var docs = store.LoadDocuments();
        Assert.Equal(3, docs.Count);
        Assert.Equal(new DocumentSessionState(@"C:\h\.claude\CLAUDE.md", 1, true, false, true, 12, 4), docs[0]);
        Assert.Equal(250, docs[1].ViewNumber);
        Assert.True(docs[2].IsPreview);
        Assert.True(store.LoadExplorers()["p1"].IsCollapsed);

        store.SaveExplorers(new Dictionary<string, ExplorerViewState>());
        Assert.Equal(3, store.LoadDocuments().Count);
    }

    [Fact]
    public void Invalid_document_entries_are_skipped()
    {
        var file = _temp.Combine("session.json");
        File.WriteAllText(file, """
            { "schemaVersion": 1, "documents": [
              1, "x", {}, { "path": "relative\\a.md" }, { "path": 5 },
              { "path": "C:\\a.md", "view": -3, "line": 0, "column": "x", "pinned": "yes" },
              { "path": "C:\\A.md", "view": 1 }
            ] }
            """);
        var store = new SessionStateStore(PhysicalFileStore.Instance, file, new ListAppLog());

        var doc = Assert.Single(store.LoadDocuments());
        Assert.Equal(new DocumentSessionState(@"C:\a.md", 1), doc);
    }

    [Fact]
    public void Document_list_is_capped()
    {
        var file = _temp.Combine("session.json");
        var store = new SessionStateStore(PhysicalFileStore.Instance, file, new ListAppLog());

        store.SaveDocuments(Enumerable.Range(0, 150).Select(i => new DocumentSessionState($@"C:\f{i}.txt", 1)).ToList());

        Assert.Equal(SessionStateStore.MaxDocuments, store.LoadDocuments().Count);
    }
}
