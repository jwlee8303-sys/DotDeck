using System.Text;
using DotDeck.App.ViewModels.Documents;
using DotDeck.App.ViewModels.Editor;
using DotDeck.Core.Documents;
using DotDeck.Core.Editors;

namespace DotDeck.App.Tests;

/// <summary>Soft wrap ("자동 줄 바꿈", Alt+Z) is display state only: it never touches the text, undo or dirty state.</summary>
public sealed class WordWrapTests
{
    private sealed class TestHost : ITextDocumentHost
    {
        public Task SaveAsync(TextDocumentViewModel document) => Task.CompletedTask;
        public void OpenNewView(TextDocumentViewModel document) { }
        public void SetEditorKind(TextDocumentViewModel document, EditorKind? kind) { }
        public void ShowFormHelp(DotDeck.App.ViewModels.Tools.RoleDescription? help) { }
    }

    /// <summary>A tab that is not a text editor (like the welcome page or a system table).</summary>
    private sealed class PlainDocument() : DotDeck.App.ViewModels.DocumentViewModel("doc:plain", "plain", _ => { });

    private static readonly byte[] s_crlfBom = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("# 제목\r\n- 긴 목록 항목 " + new string('가', 300) + "\r\n끝\r\n")];

    private static DocumentBuffer Buffer(string path = @"C:\fake\notes.md", byte[]? bytes = null, bool defaultWrap = false) =>
        new(TextFileReader.Decode(path, bytes ?? s_crlfBom, DateTime.UtcNow)) { DefaultWordWrap = defaultWrap };

    private static TextDocumentViewModel View(DocumentBuffer buffer, int number = 1, EditorKind? kind = null) =>
        new(buffer, number, isPreview: false, new TestHost(), _ => { }, kind: kind);

    [Fact]
    public void Default_comes_from_the_buffer_and_a_toggle_becomes_the_choice()
    {
        var buffer = Buffer(defaultWrap: true);
        var view = View(buffer);

        Assert.True(view.SupportsWordWrap);
        Assert.True(view.IsWordWrapEnabled);
        Assert.Null(view.WordWrapChoice);
        Assert.Equal("줄 바꿈: 켬", view.WordWrapStatusText);

        view.ToggleWordWrapCommand.Execute(null);

        Assert.False(view.IsWordWrapEnabled);
        Assert.False(view.WordWrapChoice);
        Assert.Equal("줄 바꿈: 끔", view.WordWrapStatusText);
    }

    [Fact]
    public void Toggle_does_not_dirty_the_document_or_record_undo_and_saves_the_same_bytes()
    {
        var buffer = Buffer();
        var view = View(buffer);
        var text = buffer.Document.Text;
        var changes = buffer.ChangeCount;

        view.ToggleWordWrapCommand.Execute(null);
        view.ToggleWordWrapCommand.Execute(null);
        view.ToggleWordWrapCommand.Execute(null);

        Assert.False(buffer.IsDirty);
        Assert.Equal("notes.md", view.Title);
        Assert.False(buffer.Document.UndoStack.CanUndo);
        Assert.Equal(changes, buffer.ChangeCount);
        Assert.Equal(text, buffer.Document.Text);

        // What a save writes: the document text in the file's encoding (BOM kept), line endings as typed.
        Assert.Equal(s_crlfBom, TextEncodings.Encode(buffer.Document.Text, buffer.Snapshot.Encoding));
    }

    [Fact]
    public void Two_views_of_one_file_follow_the_same_state()
    {
        var buffer = Buffer();
        var first = View(buffer, 1);
        var second = View(buffer, 2);
        var raised = new List<string?>();
        second.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        first.ToggleWordWrapCommand.Execute(null);

        Assert.True(second.IsWordWrapEnabled);
        Assert.Contains(nameof(TextDocumentViewModel.IsWordWrapEnabled), raised);
        Assert.Contains(nameof(TextDocumentViewModel.WordWrapStatusText), raised);

        second.IsWordWrapEnabled = false;

        Assert.False(first.IsWordWrapEnabled);
    }

    [Fact]
    public void Form_view_disables_the_toggle()
    {
        var buffer = Buffer(@"C:\fake\.claude\settings.json", Encoding.UTF8.GetBytes("{}"));
        var view = View(buffer, kind: EditorKind.ClaudeSettings);

        view.ShowFormCommand.Execute(null);

        Assert.True(view.IsFormView);
        Assert.False(view.SupportsWordWrap);
        Assert.False(view.ToggleWordWrapCommand.CanExecute(null));

        view.ShowTextCommand.Execute(null);

        Assert.True(view.SupportsWordWrap);
        Assert.True(view.ToggleWordWrapCommand.CanExecute(null));
    }

    [Fact]
    public void Non_text_tabs_do_not_support_wrap_and_untitled_tabs_do()
    {
        var welcome = new PlainDocument();
        var untitled = new ScratchDocumentViewModel(1, _ => { }, "abc");

        Assert.False(welcome.SupportsWordWrap);
        Assert.False(welcome.ToggleWordWrapCommand.CanExecute(null));
        Assert.True(untitled.SupportsWordWrap);

        untitled.ToggleWordWrapCommand.Execute(null);

        Assert.True(untitled.IsWordWrapEnabled);
        Assert.Equal("abc", untitled.Text);
        Assert.False(untitled.Document.UndoStack.CanUndo);
    }
}
