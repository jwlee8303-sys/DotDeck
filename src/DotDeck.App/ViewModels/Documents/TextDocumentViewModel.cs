using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Editor;
using DotDeck.Core.Editors;
using DotDeck.Core.Editors.Registry;
using DotDeck.Core.Roles;
using DotDeck.Core.Security;

namespace DotDeck.App.ViewModels.Documents;

/// <summary>Shell operations an editor tab needs (implemented by <see cref="ShellViewModel"/>).</summary>
public interface ITextDocumentHost
{
    Task SaveAsync(TextDocumentViewModel document);

    void OpenNewView(TextDocumentViewModel document);

    /// <summary>"다음 형식으로 열기": remembers <paramref name="kind"/> for the file (null: automatic) and applies it.</summary>
    void SetEditorKind(TextDocumentViewModel document, EditorKind? kind);

    /// <summary>Shows help for the selected form row in the properties pane (null clears it).</summary>
    void ShowFormHelp(Tools.RoleDescription? help);
}

/// <summary>
/// Editor tab for a file. Several tabs can show one <see cref="DocumentBuffer"/>. A preview tab (single click in the
/// explorer) is reused for the next preview until it is edited, pinned or opened explicitly.
/// </summary>
public sealed partial class TextDocumentViewModel : DocumentViewModel
{
    private readonly ITextDocumentHost _host;

    public TextDocumentViewModel(DocumentBuffer buffer, int viewNumber, bool isPreview, ITextDocumentHost host, Action<DocumentViewModel> close,
        FileRole? role = null, EditorKind? kind = null, bool isKindOverridden = false)
        : base(CreateContentId(buffer.Path, viewNumber), buffer.FileName, close)
    {
        Buffer = buffer;
        ViewNumber = viewNumber;
        _host = host;
        _isPreview = isPreview;
        Find = new FindReplaceViewModel(buffer.Document) { CanEdit = () => !buffer.IsReadOnly };
        Role = role;
        _kind = kind ?? (FileRoleResolver.IsMarkdown(buffer.Path) ? EditorKind.Markdown : EditorKind.Text);
        _isKindOverridden = isKindOverridden;
        Buffer.PropertyChanged += OnBufferPropertyChanged;
        Buffer.TextChanged += OnBufferTextChanged;
        UpdateTitle();
        CollectSecrets();
    }

    private string? _maskedText;

    /// <summary>Screen share mode only: the user revealed this view's secret values.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMaskedDisplay))]
    private bool _isSecretsRevealed;

    /// <summary>Masked copy for screen share mode (empty when the mode is off). Never written back.</summary>
    public string MaskedText => !SecretPresentation.ScreenShareMode
        ? string.Empty
        : _maskedText ??= SecretPresentation.MaskDocument(FilePath, Buffer.Document.Text, Buffer.IsSecretFile);

    /// <summary>Screen share mode is on and the document has something to mask.</summary>
    public bool HasSecrets => SecretPresentation.ScreenShareMode && MaskedText != Buffer.Document.Text;

    public bool IsMaskedDisplay => HasSecrets && !IsSecretsRevealed;

    [RelayCommand]
    private void RevealSecrets() => IsSecretsRevealed = !IsSecretsRevealed;

    /// <summary>Screen share mode was switched: revealed views are hidden again and the masked copy is rebuilt.</summary>
    public void OnScreenShareModeChanged()
    {
        IsSecretsRevealed = false;
        RefreshMask();
    }

    private void RefreshMask()
    {
        _maskedText = null;
        OnPropertyChanged(nameof(MaskedText));
        OnPropertyChanged(nameof(HasSecrets));
        OnPropertyChanged(nameof(IsMaskedDisplay));
    }

    // Only disk versions (open, reload, save) are collected: collecting while typing would register every prefix.
    private void CollectSecrets() => SecretPresentation.CollectSecrets(FilePath, Buffer.Snapshot.Text, Buffer.IsSecretFile);

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

    /// <summary>What the file is to Claude Code (SPEC §7.0), or null when it is not a known configuration file.</summary>
    public FileRole? Role { get; }

    /// <summary>Type-specific editor of this file (SPEC §7): from <see cref="Role"/>, or chosen by the user.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutline), nameof(HasForm), nameof(KindText), nameof(ToolTip))]
    [NotifyCanExecuteChangedFor(nameof(ShowFormCommand))]
    private EditorKind _kind;

    /// <summary>True when the user chose the editor ("다음 형식으로 열기").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindText), nameof(ToolTip))]
    private bool _isKindOverridden;

    /// <summary>Markdown-based documents get a table of contents.</summary>
    public bool HasOutline => EditorRegistry.Get(Kind).HasOutline;

    /// <summary>The view bar (format menu, text/form switch, table of contents).</summary>
    public bool HasViewBar => true;

    public static string KindLabel(EditorKind kind) => EditorRegistry.Get(kind).Label;

    public IReadOnlyList<EditorChoice> EditorChoices => FormRegistry.Choices(OpenAsCommand);

    /// <summary>e.g. "형식: 설정 폼 (수동 지정)".</summary>
    public string KindText => $"형식: {KindLabel(Kind)}{(IsKindOverridden ? " (수동 지정)" : string.Empty)}";

    /// <summary>Applies an editor kind (the host stores the choice). The form is rebuilt for the new kind.</summary>
    public void ApplyKind(EditorKind kind, bool overridden)
    {
        IsKindOverridden = overridden;
        if (Kind == kind)
        {
            return;
        }

        IsFormView = false;
        Form = null;
        FormError = null;
        Kind = kind;
    }

    /// <summary>Switches to the form after a manual choice; on failure the text stays with the reason shown.</summary>
    public void TryShowForm()
    {
        if (HasForm)
        {
            ShowForm();
            if (FormError is { } error)
            {
                FormError = $"{KindLabel(Kind)}으로 해석할 수 없어 텍스트로 표시합니다: {error}";
            }
        }
    }

    /// <summary>Raised to move the editor caret to a 1-based line (table of contents, validation).</summary>
    public event EventHandler<int>? GoToLineRequested;

    public IReadOnlyList<OutlineHeading> GetOutline() => MarkdownOutline.Parse(Buffer.Document.Text);

    public void GoToLine(int line)
    {
        IsFormView = false;
        GoToLineRequested?.Invoke(this, line);
    }

    /// <summary>Agents, skills and Claude settings have a form view besides the text.</summary>
    public bool HasForm => !Buffer.IsReadOnly && FormRegistry.HasForm(Kind);

    [ObservableProperty]
    private bool _isFormView;

    [ObservableProperty]
    private DocumentFormViewModel? _form;

    /// <summary>Why the form cannot be shown (e.g. invalid JSON); shown above the text editor.</summary>
    [ObservableProperty]
    private string? _formError;

    private bool _applyingForm;

    /// <summary>Recreated from session.json before the layout is restored.</summary>
    public override bool IsRestorable => true;

    public bool IsMarkdown => System.IO.Path.GetExtension(FilePath) is var ext
        && (string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase));

    /// <summary>Side-by-side Markdown preview inside this tab (Ctrl+Shift+V).</summary>
    [ObservableProperty]
    private bool _isMarkdownPreviewOpen;

    public string CaretText => $"줄 {CaretLine}, 열 {CaretColumn}";

    /// <summary>Tab tooltip: path, then role and scope (e.g. "settings.json · 프로젝트"), then a manual format choice.</summary>
    public string ToolTip
    {
        get
        {
            var lines = new List<string> { FilePath };
            if (Role is not null)
            {
                lines.Add($"{Role.Summary(FilePath)} (기준: {Role.BaseRoot})");
            }

            if (IsKindOverridden)
            {
                lines.Add(KindText);
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

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
    private void ShowText() => IsFormView = false;

    [RelayCommand(CanExecute = nameof(HasForm))]
    private void ShowForm()
    {
        if (Buffer.IsReadOnly) return;
        Form ??= FormRegistry.Create(Kind, new FormContext(ApplyFormText, FilePath, Role, _host));
        if (Form is null)
        {
            FormError = "이 형식의 폼은 아직 지원하지 않습니다.";
            IsFormView = false;
            return;
        }

        FormError = Form.Load(Buffer.Document.Text);
        IsFormView = FormError is null;
    }

    // The form writes whole text; one Replace keeps it a single undo step in the editor.
    private void ApplyFormText(string text)
    {
        if (Buffer.IsReadOnly) return;
        var document = Buffer.Document;
        if (document.Text == text)
        {
            return;
        }

        _applyingForm = true;
        try
        {
            document.Replace(0, document.TextLength, text);
        }
        finally
        {
            _applyingForm = false;
        }
    }

    /// <summary>"다음 형식으로 열기" from the view bar: text, markdown, settingsForm, agentForm, skillForm or auto.</summary>
    [RelayCommand]
    private void OpenAs(string? name) =>
        _host.SetEditorKind(this, name is null or "auto" ? null : EditorOverrideStore.NameToKind(name));

    [RelayCommand]
    private void OpenFind() => Find.Open(replace: false);

    [RelayCommand]
    private void OpenReplace() => Find.Open(replace: true);

    partial void OnIsPreviewChanged(bool value) => UpdateTitle();

    private void OnBufferTextChanged(object? sender, EventArgs e)
    {
        RefreshMask();
        Promote();

        // Text changed outside the form (reload, undo, restore): refresh the form, or fall back to text.
        if (IsFormView && !_applyingForm && Form is not null)
        {
            FormError = Form.Load(Buffer.Document.Text);
            IsFormView = FormError is null;
        }
    }

    private void OnBufferPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentBuffer.IsReadOnly))
        {
            OnPropertyChanged(nameof(HasForm));
            ShowFormCommand.NotifyCanExecuteChanged();
            if (Buffer.IsReadOnly) IsFormView = false;
        }
        if (e.PropertyName == nameof(DocumentBuffer.IsDirty))
        {
            UpdateTitle();
        }
        if (e.PropertyName == nameof(DocumentBuffer.Snapshot))
        {
            // Reload replaces the text before the snapshot, so the secret-file flag may have changed after masking.
            CollectSecrets();
            RefreshMask();
        }
    }

    private void UpdateTitle()
    {
        var name = ViewNumber > 1 ? $"{Buffer.FileName}:{ViewNumber}" : Buffer.FileName;
        Title = Buffer.IsDirty ? name + " *" : name;
    }
}
