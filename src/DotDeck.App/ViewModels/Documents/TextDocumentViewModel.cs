using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Editor;

namespace DotDeck.App.ViewModels.Documents;

/// <summary>Shell operations an editor tab needs (implemented by <see cref="ShellViewModel"/>).</summary>
public interface ITextDocumentHost
{
    Task SaveAsync(TextDocumentViewModel document);

    void OpenNewView(TextDocumentViewModel document);
}

/// <summary>
/// Editor tab for a file. Several tabs can show one <see cref="DocumentBuffer"/>. A preview tab (single click in the
/// explorer) is reused for the next preview until it is edited, pinned or opened explicitly.
/// </summary>
public sealed partial class TextDocumentViewModel : DocumentViewModel
{
    private readonly ITextDocumentHost _host;

    public TextDocumentViewModel(DocumentBuffer buffer, int viewNumber, bool isPreview, ITextDocumentHost host, Action<DocumentViewModel> close)
        : base(CreateContentId(buffer.Path, viewNumber), buffer.FileName, close)
    {
        Buffer = buffer;
        ViewNumber = viewNumber;
        _host = host;
        _isPreview = isPreview;
        Find = new FindReplaceViewModel(buffer.Document);
        Buffer.PropertyChanged += OnBufferPropertyChanged;
        Buffer.TextChanged += OnBufferTextChanged;
        UpdateTitle();
    }

    public static string CreateContentId(string path, int viewNumber) => $"doc:{path}#{viewNumber}";

    public DocumentBuffer Buffer { get; }

    public string FilePath => Buffer.Path;

    public int ViewNumber { get; }

    public FindReplaceViewModel Find { get; }

    [ObservableProperty]
    private bool _isPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClose))]
    private bool _isPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaretText))]
    private int _caretLine = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaretText))]
    private int _caretColumn = 1;

    public bool CanClose => !IsPinned;

    /// <summary>Recreated from session.json before the layout is restored.</summary>
    public override bool IsRestorable => true;

    public bool IsMarkdown => System.IO.Path.GetExtension(FilePath) is var ext
        && (string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase));

    /// <summary>Side-by-side Markdown preview inside this tab (Ctrl+Shift+V).</summary>
    [ObservableProperty]
    private bool _isMarkdownPreviewOpen;

    public string CaretText => $"줄 {CaretLine}, 열 {CaretColumn}";

    public string ToolTip => FilePath;

    /// <summary>Turns a preview tab into a normal one (edit, double click, pin).</summary>
    public void Promote() => IsPreview = false;

    /// <summary>Detaches from the buffer; called once when the tab is closed.</summary>
    public void Detach()
    {
        Buffer.PropertyChanged -= OnBufferPropertyChanged;
        Buffer.TextChanged -= OnBufferTextChanged;
        Find.Editor = null;
    }

    [RelayCommand]
    private Task SaveAsync() => _host.SaveAsync(this);

    [RelayCommand]
    private void NewView() => _host.OpenNewView(this);

    [RelayCommand]
    private void TogglePin()
    {
        IsPinned = !IsPinned;
        if (IsPinned)
        {
            Promote();
        }
    }

    [RelayCommand(CanExecute = nameof(IsMarkdown))]
    private void ToggleMarkdownPreview() => IsMarkdownPreviewOpen = !IsMarkdownPreviewOpen;

    [RelayCommand]
    private void OpenFind() => Find.Open(replace: false);

    [RelayCommand]
    private void OpenReplace() => Find.Open(replace: true);

    partial void OnIsPreviewChanged(bool value) => UpdateTitle();

    private void OnBufferTextChanged(object? sender, EventArgs e) => Promote();

    private void OnBufferPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentBuffer.IsDirty))
        {
            UpdateTitle();
        }
    }

    private void UpdateTitle()
    {
        var name = ViewNumber > 1 ? $"{Buffer.FileName}:{ViewNumber}" : Buffer.FileName;
        Title = Buffer.IsDirty ? name + " *" : name;
    }
}
