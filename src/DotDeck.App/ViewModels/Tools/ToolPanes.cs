namespace DotDeck.App.ViewModels.Tools;

/// <summary>Project area: stacked explorers (Phase 1b).</summary>
public sealed class ProjectsPaneViewModel() : ToolPaneViewModel("tool.projects", "프로젝트", ToolLocation.Left);

/// <summary>Selected item information, validation summary, quick actions.</summary>
public sealed class PropertiesPaneViewModel() : ToolPaneViewModel("tool.properties", "속성", ToolLocation.Right);

public sealed class ValidationPaneViewModel() : ToolPaneViewModel("tool.validation", "검증 결과", ToolLocation.Bottom);

public sealed class SearchResultsPaneViewModel() : ToolPaneViewModel("tool.search", "검색 결과", ToolLocation.Bottom);

public sealed class HookTestPaneViewModel() : ToolPaneViewModel("tool.hooktest", "훅 테스트", ToolLocation.Bottom);
