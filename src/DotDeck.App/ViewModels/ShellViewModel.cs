using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.Services;
using DotDeck.App.ViewModels.Documents;
using DotDeck.App.ViewModels.Explorer;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using DotDeck.Core.Logging;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;

namespace DotDeck.App.ViewModels;

/// <summary>Services created by the composition root (App.OnStartup).</summary>
public sealed record ShellServices(
    AppPaths Paths,
    IAppLog Log,
    IFileStore Store,
    ProjectRegistry Registry,
    SessionStateStore Session,
    IClipboardService Clipboard,
    IShellLauncher Shell);

/// <summary>Root view model: owns the documents and tool panes bound to the docking manager.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IAppLog _log;
    private int _scratchCounter;

    public ShellViewModel(ShellServices services, string version, bool isAdministrator)
    {
        ArgumentNullException.ThrowIfNull(services);
        Paths = services.Paths;
        _log = services.Log;
        Version = version;
        IsAdministrator = isAdministrator;

        var explorerContext = new ExplorerContext(
            services.Registry, services.Paths, services.Store, services.Clipboard, services.Shell, services.Log,
            message => StatusMessage = message);
        Projects = new ProjectsPaneViewModel(explorerContext, services.Session);
        Properties = new PropertiesPaneViewModel();
        Output = new OutputPaneViewModel(services.Log);
        Validation = new ValidationPaneViewModel();
        SearchResults = new SearchResultsPaneViewModel();
        HookTest = new HookTestPaneViewModel();
        Tools = [Projects, Properties, Output, Validation, SearchResults, HookTest];

        Documents.Add(new WelcomeDocumentViewModel(Paths, version, CloseDocument));
    }

    /// <summary>Runs once the main window is shown: opens the projects and restores their state.</summary>
    public Task InitializeAsync() => Projects.InitializeAsync();

    /// <summary>Saves view state that is not part of the docking layout (called on close).</summary>
    public void SaveSession() => Projects.SaveState();

    public void ReportError(string message, Exception ex)
    {
        _log.Error(message, ex);
        StatusMessage = $"{message} {ex.Message}";
    }

    public AppPaths Paths { get; }

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
            ?? Documents.FirstOrDefault(d => d.ContentId == contentId && d.IsRestorable);
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
        Documents.Remove(document);
        _log.Debug($"Closed document {document.ContentId}");
    }
}
