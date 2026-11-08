using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.Services;
using DotDeck.App.ViewModels.Documents;
using DotDeck.App.ViewModels.Editor;
using DotDeck.App.ViewModels.Explorer;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.FileOperations;
using DotDeck.Core.Files;
using DotDeck.Core.Logging;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;
using DotDeck.Core.Roles;

namespace DotDeck.App.ViewModels;

/// <summary>Services created by the composition root (App.OnStartup).</summary>
public sealed record ShellServices(
    AppPaths Paths,
    IAppLog Log,
    IFileStore Store,
    ProjectRegistry Registry,
    SessionStateStore Session,
    IClipboardService Clipboard,
    IShellLauncher Shell,
    IDialogService Dialogs,
    FileOperationService FileOperations,
    TextFileReader Reader,
    SafeWriter Writer,
    BackupStore Backups,
    DotDeck.Core.SystemConfig.IEnvironmentStore Environment);

/// <summary>Root view model: owns the documents and tool panes bound to the docking manager.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IAppLog _log;
    private readonly IDialogService _dialogs;
    private readonly DocumentBufferManager _buffers;
    private readonly SafeWriter _writer;
    private readonly BackupStore _backups;
    private readonly IFileStore _store;
    private readonly SessionStateStore _session;
    private readonly DotDeck.Core.SystemConfig.IEnvironmentStore _environment;
    private readonly SynchronizationContext? _ui;
    private readonly FileRoleResolver _roles;
    private readonly EditorOverrideStore _editorOverrides;
    private int _scratchCounter;

    public ShellViewModel(ShellServices services, string version, bool isAdministrator)
    {
        ArgumentNullException.ThrowIfNull(services);
        Paths = services.Paths;
        _log = services.Log;
        _dialogs = services.Dialogs;
        _buffers = new DocumentBufferManager(services.Reader);
        _writer = services.Writer;
        _backups = services.Backups;
        _store = services.Store;
        _session = services.Session;
        _environment = services.Environment;
        _ui = SynchronizationContext.Current;
        Version = version;
        IsAdministrator = isAdministrator;

        // Claude Code file roles (SPEC 7.0): user copies in %APPDATA%\DotDeck\catalogs replace the built-in data.
        var roleCatalog = Core.Catalogs.CatalogResources.Load(FileRoleCatalog.ClaudeResource, FileRoleCatalog.Parse, services.Store, services.Paths.CatalogsDir);
        var keyCatalog = Core.Catalogs.CatalogResources.Load(Core.Editors.SettingsKeyCatalog.ClaudeResource, Core.Editors.SettingsKeyCatalog.Parse, services.Store, services.Paths.CatalogsDir);
        var toolCatalog = Core.Catalogs.CatalogResources.Load(Core.Editors.ClaudeToolCatalog.ClaudeResource, Core.Editors.ClaudeToolCatalog.Parse, services.Store, services.Paths.CatalogsDir);
        var eventCatalog = Core.Catalogs.CatalogResources.Load(Core.Editors.HookEventCatalog.ClaudeResource, Core.Editors.HookEventCatalog.Parse, services.Store, services.Paths.CatalogsDir);
        foreach (var warning in new[] { roleCatalog.Warning, keyCatalog.Warning, toolCatalog.Warning, eventCatalog.Warning }.OfType<string>())
        {
            _log.Warning(warning);
        }

        SettingsKeys = keyCatalog.Value;
        ToolCatalog = toolCatalog.Value;
        HookEvents = eventCatalog.Value;

        // Managed MCP servers only for the real home: a sandbox home (--home) never reads machine-wide files.
        var managedMcp = !services.Paths.IsHomeOverridden && roleCatalog.Value.ManagedDir is { } managedDir
            ? System.IO.Path.Combine(managedDir, "managed-mcp.json")
            : null;
        _mcp = new Core.Mcp.McpServerDiscovery(services.Store, services.Paths.Home, managedMcp);
        _roles = new FileRoleResolver([roleCatalog.Value], services.Store, services.Paths.Home, ProjectRoots,
            includeManaged: !services.Paths.IsHomeOverridden);
        _editorOverrides = new EditorOverrideStore(services.Store, services.Paths.EditorOverridesFile);

        ExplorerPreferences.SetGlobalExtensions(new DotDeck.Core.Settings.AppSettingsStore(services.Store, services.Paths.SettingsFile).Load().DefaultExtensions);
        var explorerContext = new ExplorerContext(
            new ProjectRegistryGateway(services.Registry), services.Paths, services.Store, services.Clipboard,
            services.Shell, services.Dialogs,
            new ExplorerFileOperations(services.FileOperations, services.Dialogs, services.Log, message => StatusMessage = message),
            services.Log, message => StatusMessage = message, new FolderWatcherFactory(), ExplorerPreferences, OpenFileAsync, OpenSystemNode,
            RegisterCustomFileAsync, RemoveCustomFileAsync, OpenFileAsAsync);
        Properties = new PropertiesPaneViewModel { DescribeRole = DescribeRole };
        Output = new OutputPaneViewModel(services.Log);
        Validation = new ValidationPaneViewModel();
        SearchResults = new SearchResultsPaneViewModel(services.Store, SearchRoots, OpenSearchHitAsync);
        HookTest = new HookTestPaneViewModel { Dialogs = services.Dialogs, WorkingDirectory = services.Paths.Home };
        Tools = [Properties, Output, Validation, SearchResults, HookTest];
        Projects = new ProjectsPaneViewModel(explorerContext, services.Session, Tools.Add, tool => Tools.Remove(tool));
        Tools.Insert(0, Projects);
        Projects.List.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Projects.List.SelectedRow))
            {
                RefreshProjectRoots();
                Properties.Show((Projects.List.SelectedRow as NodeRowViewModel)?.Node.FullPath);
            }
        };

        Documents.Add(new WelcomeDocumentViewModel(Paths, version, CloseDocument));
    }

    /// <summary>Creates dynamic tool panes (detached explorers) that the saved docking layout refers to.</summary>
    public void PrepareLayoutContent() => Projects.PrepareDetachedPanes();

    /// <summary>Runs once the main window is shown: opens the projects and restores their state.</summary>
    public Task InitializeAsync() => Projects.InitializeAsync();

    public void ReportError(string message, Exception ex)
    {
        _log.Error(message, ex);
        StatusMessage = $"{message} {ex.Message}";
    }

    public AppPaths Paths { get; }

    /// <summary>Explorer settings changed by the settings dialog (global default extensions).</summary>
    public ExplorerPreferences ExplorerPreferences { get; } = new();

    /// <summary>Dialogs for view-level commands (layout menu) that need no view model of their own.</summary>
    public IDialogService Dialogs => _dialogs;

    public string Version { get; }

    public bool IsAdministrator { get; }

    /// <summary>True when running against a substitute home (status bar turns orange as a warning sign).</summary>
    public bool IsSandbox => Paths.IsHomeOverridden;

    public string Title => Paths.IsHomeOverridden ? $"DotDeck — 샌드박스: {Paths.Home}" : "DotDeck";

    public string HomeStatusText => Paths.IsHomeOverridden ? $"샌드박스 홈: {Paths.Home}" : $"홈: {Paths.Home}";

    public string PrivilegeStatusText => IsAdministrator ? "관리자" : "표준 사용자";

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    public ObservableCollection<ToolPaneViewModel> Tools { get; }

    public ProjectsPaneViewModel Projects { get; }

    public PropertiesPaneViewModel Properties { get; }

    public OutputPaneViewModel Output { get; }

    public ValidationPaneViewModel Validation { get; }

    public SearchResultsPaneViewModel SearchResults { get; }

    public HookTestPaneViewModel HookTest { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveActiveCommand))]
    private PaneViewModel? _activeContent;

    [ObservableProperty]
    private string _statusMessage = "준비";

    /// <summary>Finds content for a ContentId from a saved layout. Returns null when it cannot be restored.</summary>
    public PaneViewModel? ResolveContent(string? contentId)
    {
        if (string.IsNullOrEmpty(contentId))
        {
            return null;
        }

        return (PaneViewModel?)Tools.FirstOrDefault(t => t.ContentId == contentId)
            ?? Documents.FirstOrDefault(d => d.ContentId == contentId && d.IsRestorable)
            ?? CreateSystemDocument(contentId);
    }

    [RelayCommand]
    private void NewScratchDocument()
    {
        var doc = new ScratchDocumentViewModel(++_scratchCounter, CloseDocument);
        Documents.Add(doc);
        ActiveContent = doc;
    }

    [RelayCommand]
    private void ShowWelcome()
    {
        var welcome = Documents.OfType<WelcomeDocumentViewModel>().FirstOrDefault();
        if (welcome is null)
        {
            welcome = new WelcomeDocumentViewModel(Paths, Version, CloseDocument);
            Documents.Add(welcome);
        }

        ActiveContent = welcome;
    }

    [RelayCommand]
    private static void ToggleTool(ToolPaneViewModel? tool)
    {
        if (tool is not null)
        {
            tool.IsVisible = !tool.IsVisible;
            if (tool.IsVisible)
            {
                tool.IsActive = true;
            }
        }
    }

    private void CloseDocument(DocumentViewModel document)
    {
        if (document is TextDocumentViewModel text)
        {
            if (TryCloseTextDocument(text))
            {
                _log.Debug($"Closed document {document.ContentId}");
            }

            return;
        }

        if (document is ScratchDocumentViewModel untitled)
        {
            _ = CloseUntitledAsync(untitled);
            return;
        }

        if (document is SystemDocumentViewModel { IsDirty: true } system
            && !_dialogs.Confirm("닫기", $"{system.Title}에 적용하지 않은 변경이 있습니다. 버리고 닫을까요?"))
        {
            return;
        }

        Documents.Remove(document);
        _log.Debug($"Closed document {document.ContentId}");
    }
}
