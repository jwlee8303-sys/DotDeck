using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Explorer;

namespace DotDeck.App.ViewModels.Explorer;

/// <summary>One row of the flattened project list (DESIGN 5, option A).</summary>
public abstract partial class ExplorerRowViewModel : ObservableObject
{
    protected ExplorerRowViewModel(ExplorerViewModel explorer, int depth)
    {
        Explorer = explorer;
        Depth = depth;
    }

    public ExplorerViewModel Explorer { get; }

    /// <summary>Indentation level below the project header (0 = root children).</summary>
    public int Depth { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Project section header: name, color, collapse toggle and the explorer toolbar.</summary>
public sealed partial class ProjectHeaderRowViewModel : ExplorerRowViewModel
{
    public ProjectHeaderRowViewModel(ExplorerViewModel explorer)
        : base(explorer, -1)
    {
    }

    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>#RRGGBB accent, or null.</summary>
    [ObservableProperty]
    private string? _color;

    [ObservableProperty]
    private bool _isCollapsed;

    [ObservableProperty]
    private string? _rootPath;

    [ObservableProperty]
    private bool _isConfigDocsFilter;

    [ObservableProperty]
    private bool _isLoading;
}

/// <summary>A file, folder or virtual node.</summary>
public sealed partial class NodeRowViewModel : ExplorerRowViewModel
{
    public NodeRowViewModel(ExplorerViewModel explorer, NodeInfo node, int depth, bool isExpanded, string? error)
        : base(explorer, depth)
    {
        Node = node;
        _isExpanded = isExpanded;
        _error = error;
    }

    public NodeInfo Node { get; }

    public string Key => ExplorerTreeBuilder.KeyOf(Node);

    public string Name => Node.Name;

    public bool IsFolder => Node.Kind == NodeKind.Folder;

    public bool IsEnabled => Node.IsEnabled;

    public string ToolTipText => Error is null
        ? Node.FullPath ?? Node.Name
        : $"{Node.FullPath ?? Node.Name}\n{Error}";

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>True after expanding showed no children; the chevron is then hidden (VS behavior).</summary>
    [ObservableProperty]
    private bool _isKnownEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string? _error;
}

/// <summary>Informational row inside a project ("폴더 없음 — 만들기", "(비어 있음)", errors).</summary>
public sealed class MessageRowViewModel : ExplorerRowViewModel
{
    public MessageRowViewModel(ExplorerViewModel explorer, string text, bool isError = false,
        string? actionText = null, IRelayCommand? action = null)
        : base(explorer, 0)
    {
        Text = text;
        IsError = isError;
        ActionText = actionText;
        Action = action;
    }

    public string Text { get; }

    public bool IsError { get; }

    public string? ActionText { get; }

    public IRelayCommand? Action { get; }
}
