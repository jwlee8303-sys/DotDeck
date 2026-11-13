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

    [Fact]
    public void Terminal_identity_title_and_folder_round_trip_alongside_other_state()
    {
        var file = _temp.Combine("session.json");
        var store = new SessionStateStore(PhysicalFileStore.Instance, file, new ListAppLog());
        store.SaveDocuments([new(@"C:\project\readme.md", 1)]);
        store.SaveTerminals([new("ai-1", "Codex · 프로젝트", @"C:\project\한글 폴더")]);
        store.SaveExplorers(new Dictionary<string, ExplorerViewState> { ["p"] = new(false, ["src"], null) });

        var reloaded = new SessionStateStore(PhysicalFileStore.Instance, file, new ListAppLog());
        Assert.Equal(new TerminalSessionState("ai-1", "Codex · 프로젝트", @"C:\project\한글 폴더"), Assert.Single(reloaded.LoadTerminals()));
        Assert.Single(reloaded.LoadDocuments());
        Assert.Single(reloaded.LoadExplorers());

        reloaded.SaveTerminals([]);
        Assert.Empty(store.LoadTerminals());
        Assert.Single(store.LoadDocuments());
    }

    [Fact]
    public void Terminal_load_skips_invalid_entries_and_duplicate_ids()
    {
        var file = _temp.Combine("session.json");
        File.WriteAllText(file, """
            { "terminals": [ {}, 42,
              { "id": "a", "title": "PowerShell", "workingDirectory": "relative" },
              { "id": "a", "title": "PowerShell", "workingDirectory": "C:\\project" },
              { "id": "a", "title": "duplicate", "workingDirectory": "C:\\other" },
              { "id": "b", "title": "", "workingDirectory": "C:\\project" }
            ] }
            """, System.Text.Encoding.UTF8);
        var store = new SessionStateStore(PhysicalFileStore.Instance, file, new ListAppLog());

        Assert.Equal(new TerminalSessionState("a", "PowerShell", @"C:\project"), Assert.Single(store.LoadTerminals()));
    }

    [Fact]
    public void Terminal_list_is_bounded_and_newer_schema_is_not_overwritten()
    {
        var file = _temp.Combine("session.json");
        var store = new SessionStateStore(PhysicalFileStore.Instance, file, new ListAppLog());
        var terminals = Enumerable.Range(0, 150).Select(i => new TerminalSessionState(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "PowerShell", @"C:\project")).ToList();
        store.SaveTerminals(terminals);
        Assert.Equal(SessionStateStore.MaxTerminals, store.LoadTerminals().Count);

        const string newer = "{\"schemaVersion\":999,\"future\":true}";
        File.WriteAllText(file, newer, System.Text.Encoding.UTF8);
        store.SaveTerminals([]);
        Assert.Equal(newer, File.ReadAllText(file, System.Text.Encoding.UTF8));
    }
}
