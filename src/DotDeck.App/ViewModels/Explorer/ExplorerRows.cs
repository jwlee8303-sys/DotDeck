using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Explorer;
using DotDeck.Core.Explorer.Visibility;

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

    /// <summary>Highlighted while a drag hovers over the folder it would drop into.</summary>
    [ObservableProperty]
    private bool _isDropTarget;
}

/// <summary>A row whose name is being typed in place (rename, or a new item).</summary>
public interface IEditableRow
{
    bool IsEditing { get; set; }

    string EditText { get; set; }
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
    private bool _isLoading;

    /// <summary>Edit mode: the project root's check box (null outside edit mode).</summary>
    [ObservableProperty]
    private CheckState? _checkState;
}

/// <summary>One folder of a merged compact-tree row ("src/App/Themes"); clicking it targets that folder.</summary>
public sealed record ChainSegment(int Index, string Name, NodeRowViewModel Row, bool IsLast);

/// <summary>A file, folder or virtual node.</summary>
public sealed partial class NodeRowViewModel : ExplorerRowViewModel, IEditableRow
{
    /// <summary>True while the name is edited in place (F2).</summary>
    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = string.Empty;

    /// <summary>A file system item (not a virtual node) that file operations can act on.</summary>
    public bool IsFileSystemItem => Node.Kind != NodeKind.Virtual && Node.FullPath is not null;

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

    public bool HasSegments => Node.Chain is { Count: > 1 };

    /// <summary>Compact tree: the merged folders as clickable parts; null for an ordinary row.</summary>
    public IReadOnlyList<ChainSegment>? Segments => Node.Chain is { Count: > 1 } chain
        ? [.. chain.Select((n, i) => new ChainSegment(i, n.Name, this, i == chain.Count - 1))]
        : null;

    /// <summary>Flat view: the file's folder relative to the root (shown dimmed); null at the root or in other views.</summary>
    public string? FlatDirectoryText => Node.FlatDirectory is { Length: > 0 } directory ? directory : null;

    /// <summary>The clicked part of a merged compact row (null: the last folder, which is the row itself).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetNode))]
    private int? _activeSegment;

    /// <summary>The item commands act on: the clicked folder of a merged row, else <see cref="Node"/>.</summary>
    public NodeInfo TargetNode => ActiveSegment is { } index && Node.Chain is { } chain && index >= 0 && index < chain.Count
        ? chain[index]
        : Node;

    /// <summary>Edit mode: check box state (null outside edit mode). Unchecked rows are shown dimmed.</summary>
    [ObservableProperty]
    private CheckState? _checkState;

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

/// <summary>Placeholder row where the name of a new file or folder is typed (removed on commit or cancel).</summary>
public sealed partial class NewItemRowViewModel : ExplorerRowViewModel, IEditableRow
{
    public NewItemRowViewModel(ExplorerViewModel explorer, int depth, string parentDirectory, bool isFolder)
        : base(explorer, depth)
    {
        ParentDirectory = parentDirectory;
        IsFolder = isFolder;
    }

    public string ParentDirectory { get; }

    public bool IsFolder { get; }

    [ObservableProperty]
    private bool _isEditing = true;

    [ObservableProperty]
    private string _editText = string.Empty;
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
