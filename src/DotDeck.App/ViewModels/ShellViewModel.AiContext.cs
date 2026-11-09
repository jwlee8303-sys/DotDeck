using System.IO;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.ViewModels.Documents;
using DotDeck.Core.AiCli.Mcp;
using DotDeck.Core.AiCli.Scope;
using DotDeck.Core.AiCli.Sessions;
using DotDeck.Core.Explorer;

namespace DotDeck.App.ViewModels;

public sealed partial class ShellViewModel
{
    internal EditorContext ReadAiEditorContext()
    {
        EditorDocument? Describe(TextDocumentViewModel? doc, bool includeSelection)
        {
            if (doc is null || doc.Buffer.IsSecretFile) return null;
            if (!includeSelection) return new EditorDocument(doc.FilePath);
            var selection = doc.Find.Editor;
            var start = selection?.SelectionStart ?? 0;
            var length = selection?.SelectionLength ?? 0;
            var text = length > 0 && start >= 0 && start + length <= doc.Buffer.Document.TextLength
                ? doc.Buffer.Document.GetText(start, Math.Min(length, 16000)) : null;
            return new EditorDocument(doc.FilePath, text, start, length);
        }

        return new EditorContext(Documents.OfType<TextDocumentViewModel>().Select(d => Describe(d, false)).OfType<EditorDocument>()
            .DistinctBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToArray(), Describe(ActiveTextDocument, true));
    }

    internal async Task OpenAiFileAsync(AiSession session, string path)
    {
        var originalScope = session.Scope;
        bool Authorized()
        {
            if (session.State != AiSessionState.Running || !ReferenceEquals(originalScope, session.Scope)
                || SecretFileResolver.IsSecretFile(_store, path)) return false;
            var checker = new ScopeChecker(session.Scope.Projects.Select(p => new ScopeRoot(p.Root)), WindowsFinalPathResolver.Instance);
            return checker.Check(path, session.Scope.Primary.Root, ScopeAccess.Read).IsInside;
        }
        await OpenFileCoreAsync(path, preview: false, Authorized).ConfigureAwait(true);
        if (!Authorized() || ActiveTextDocument is not { } active || !string.Equals(active.FilePath, path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("파일을 열지 못했습니다.");
    }
}
