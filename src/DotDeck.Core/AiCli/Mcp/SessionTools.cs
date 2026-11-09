using System.Text.Json;
using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.Scope;
using DotDeck.Core.Explorer;
using DotDeck.Core.Explorer.Visibility;
using DotDeck.Core.Files;
using DotDeck.Core.Projects;

namespace DotDeck.Core.AiCli.Mcp;

public sealed record EditorDocument(string Path, string? Selection = null, int? SelectionStart = null, int? SelectionLength = null);
public sealed record EditorContext(IReadOnlyList<EditorDocument> OpenDocuments, EditorDocument? ActiveDocument);

/// <summary>UI-independent tools. The caller serializes scope changes with each call.</summary>
public sealed class SessionTools(
    Func<SessionScope?> scope,
    Func<IReadOnlyList<ProjectDefinition>> projects,
    IFileStore store,
    IFinalPathResolver resolver,
    Func<CancellationToken, Task<EditorContext>> editor,
    Func<string, CancellationToken, Task> open,
    IReadOnlyList<string>? globalExtensions = null,
    bool includeOpenDocuments = true) : IAiSessionHost
{
    public async Task<JsonNode?> CallAsync(string tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        var current = scope() ?? throw new InvalidOperationException("세션이 종료되었습니다.");
        var checker = new ScopeChecker(current.Projects.Select(p => new ScopeRoot(p.Root)), resolver);
        bool Allowed(string path)
        {
            if (!Path.IsPathFullyQualified(path) || SecretFileResolver.IsSecretFile(store, path)) return false;
            var decision = checker.Check(path, current.Primary.Root, ScopeAccess.Read);
            return decision.IsInside && decision.FinalPath is { } final && !SecretFilePolicy.IsSecret(Path.GetFileName(final));
        }

        switch (tool)
        {
            case "list_projects":
                Require(arguments, []);
                return JsonSerializer.SerializeToNode(current.Projects.Select(p =>
                {
                    var definition = projects().FirstOrDefault(d => d.Id == p.ProjectId);
                    return new
                    {
                        id = p.ProjectId, name = p.Name, root = p.Root,
                        primary = p.ProjectId == current.Primary.ProjectId,
                        excludedFolders = definition is null ? Array.Empty<string>() : definition.ExcludedFolders ?? ExcludedFolderPolicy.DefaultsFor(definition),
                        showAllFiles = definition?.Visibility.ShowAllFiles ?? false,
                        extensions = definition?.Visibility.EffectiveExtensions(globalExtensions),
                    };
                }));
            case "get_open_documents":
            case "get_active_document":
                Require(arguments, []);
                if (!includeOpenDocuments) return tool == "get_open_documents" ? new JsonArray() : null;
                var state = await editor(cancellationToken).ConfigureAwait(false);
                object? Describe(EditorDocument? doc) => doc is null || !Allowed(doc.Path) ? null : new
                {
                    path = doc.Path,
                    project = current.Projects.Where(p => IsUnder(doc.Path, p.Root)).OrderByDescending(p => p.Root.Length).FirstOrDefault()?.ProjectId,
                    selection = doc.Selection is { Length: > 16000 } selection ? selection[..16000] : doc.Selection,
                    selectionStart = doc.SelectionStart, selectionLength = doc.SelectionLength,
                };
                return tool == "get_active_document"
                    ? JsonSerializer.SerializeToNode(Describe(state.ActiveDocument))
                    : JsonSerializer.SerializeToNode(state.OpenDocuments.Select(Describe).Where(d => d is not null));
            case "open_in_editor":
                Require(arguments, ["path"]);
                var requestedPath = String(arguments, "path");
                if (!Path.IsPathFullyQualified(requestedPath)) throw new ArgumentException("절대 경로를 지정하세요.");
                var path = Path.GetFullPath(requestedPath);
                if (!Allowed(path) || !store.FileExists(path)) throw new ArgumentException("허용되지 않은 파일입니다.");
                await open(path, cancellationToken).ConfigureAwait(false);
                return new JsonObject { ["opened"] = true };
            case "find_files":
                Require(arguments, ["project", "pattern"]);
                var project = String(arguments, "project");
                var candidates = current.Projects.Where(p => p.ProjectId == project).ToList();
                if (candidates.Count == 0) candidates = current.Projects.Where(p => p.Name.Equals(project, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count != 1) throw new ArgumentException("프로젝트 ID를 지정하세요.");
                var chosen = candidates[0];
                var definition = projects().FirstOrDefault(p => p.Id == chosen.ProjectId) ?? throw new InvalidOperationException();
                var pattern = String(arguments, "pattern");
                if (pattern.Length > 512 || pattern.Contains(':') || pattern.Contains("..", StringComparison.Ordinal)) throw new ArgumentException("잘못된 패턴입니다.");
                var source = new TrackingSource(new FolderProjectSource(definition, chosen.Root, store));
                var visibility = new VisibilityEvaluator(definition.Visibility, definition.Visibility.EffectiveExtensions(globalExtensions));
                var visible = new VisibleProjectSource(source, store, visibility, new ProjectViewSettings { Mode = ExplorerViewMode.Tree });
                var found = ExplorerFileSearch.Find(visible, FileNameFilter.Parse(pattern) ?? throw new ArgumentException("패턴이 없습니다."), cancellationToken: cancellationToken);
                var paths = found.Files.Where(n => n.Kind == NodeKind.File && !n.IsSecret && n.FullPath is { } file && Allowed(file)).Select(n => n.FullPath!).ToArray();
                return JsonSerializer.SerializeToNode(new
                {
                    files = paths, count = paths.Length,
                    truncated = found.IsTruncated || visible.IsTruncated,
                    incomplete = source.HasErrors || found.RootError is not null || found.RootState != ProjectRootState.Ready,
                });
            default: throw new ArgumentException("알 수 없는 도구입니다.");
        }
    }

    private static bool IsUnder(string path, string root) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string String(JsonObject args, string key) => args[key] is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
        ? text : throw new ArgumentException("필수 문자열 인수가 없습니다.");
    private static void Require(JsonObject args, string[] names)
    {
        if (args.Count != names.Length || names.Any(n => !args.ContainsKey(n))) throw new ArgumentException("잘못된 인수입니다.");
    }

    private sealed class TrackingSource(IProjectSource inner) : IProjectSource
    {
        public bool HasErrors { get; private set; }
        public ProjectDefinition Project => inner.Project;
        public string? RootPath => inner.RootPath;
        public ProjectRootState GetRootState() => inner.GetRootState();
        public IReadOnlyList<NodeInfo> GetChildren(NodeInfo? parent)
        {
            try { return inner.GetChildren(parent); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                HasErrors = true;
                throw;
            }
        }
    }
}
