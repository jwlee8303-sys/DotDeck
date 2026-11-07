using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Documents;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.Logging;
using DotDeck.Core.Paths;

namespace DotDeck.App.ViewModels;

/// <summary>Root view model: owns the documents and tool panes bound to the docking manager.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IAppLog _log;
    private int _scratchCounter;

    public ShellViewModel(AppPaths paths, IAppLog log, string version, bool isAdministrator)
    {
        Paths = paths;
        _log = log;
        Version = version;
        IsAdministrator = isAdministrator;

        Projects = new ProjectsPaneViewModel();
        Properties = new PropertiesPaneViewModel();
        Output = new OutputPaneViewModel(log);
        Validation = new ValidationPaneViewModel();
        SearchResults = new SearchResultsPaneViewModel();
        HookTest = new HookTestPaneViewModel();
        Tools = [Projects, Properties, Output, Validation, SearchResults, HookTest];

        Documents.Add(new WelcomeDocumentViewModel(paths, version, CloseDocument));
    }

    public AppPaths Paths { get; }

    public string Version { get; }

    public bool IsAdministrator { get; }

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
