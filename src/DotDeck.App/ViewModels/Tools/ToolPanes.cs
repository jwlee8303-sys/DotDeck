namespace DotDeck.App.ViewModels.Tools;

// The project area is DotDeck.App.ViewModels.Explorer.ProjectsPaneViewModel.

/// <summary>Selected item information, validation summary, quick actions.</summary>
public sealed class PropertiesPaneViewModel() : ToolPaneViewModel("tool.properties", "속성", ToolLocation.Right);


public sealed class SearchResultsPaneViewModel() : ToolPaneViewModel("tool.search", "검색 결과", ToolLocation.Bottom);

public sealed class HookTestPaneViewModel() : ToolPaneViewModel("tool.hooktest", "훅 테스트", ToolLocation.Bottom);
