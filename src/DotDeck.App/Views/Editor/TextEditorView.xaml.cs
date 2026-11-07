using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DotDeck.App.Editor;
using DotDeck.App.ViewModels.Documents;
using DotDeck.App.ViewModels.Editor;

namespace DotDeck.App.Views.Editor;

/// <summary>
/// AvalonEdit host for a <see cref="TextDocumentViewModel"/>. Pushes caret position to the view model and exposes the
/// selection through <see cref="IEditorSelection"/>. AvalonDock may reuse or recreate this view, so wiring follows
/// <see cref="FrameworkElement.DataContext"/>.
/// </summary>
public partial class TextEditorView : UserControl, IEditorSelection
{
    private TextDocumentViewModel? _viewModel;

    public TextEditorView()
    {
        InitializeComponent();
        ApplyEditorTheme();
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaret();
        DataContextChanged += (_, _) => Attach(DataContext as TextDocumentViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as TextDocumentViewModel);
    }

    int IEditorSelection.SelectionStart => Editor.SelectionStart;

    int IEditorSelection.SelectionLength => Editor.SelectionLength;

    int IEditorSelection.CaretOffset => Editor.CaretOffset;

    void IEditorSelection.Select(int start, int length)
    {
        Editor.Select(start, length);
        Editor.TextArea.Caret.Offset = start + length;
        Editor.TextArea.Caret.BringCaretToView();
    }

    void IEditorSelection.FocusEditor() => Editor.TextArea.Focus();

    private void Attach(TextDocumentViewModel? viewModel)
    {
        if (ReferenceEquals(viewModel, _viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.Find.FocusRequested -= OnFindFocusRequested;
            if (ReferenceEquals(_viewModel.Find.Editor, this))
            {
                _viewModel.Find.Editor = null;
            }
        }

        _viewModel = viewModel;
        if (viewModel is null)
        {
            return;
        }

        viewModel.Find.Editor = this;
        viewModel.Find.FocusRequested += OnFindFocusRequested;
        Editor.SyntaxHighlighting = HighlightingCatalog.ForPath(viewModel.FilePath);
        UpdateCaret();

        // Give the editor focus when its tab becomes active (opening a file, switching tabs).
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsVisible && _viewModel is { IsActive: true })
            {
                Editor.TextArea.Focus();
            }
        });
    }

    private void OnFindFocusRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        });

    private void UpdateCaret()
    {
        if (_viewModel is not null)
        {
            _viewModel.CaretLine = Editor.TextArea.Caret.Line;
            _viewModel.CaretColumn = Editor.TextArea.Caret.Column;
        }
    }

    private void ApplyEditorTheme()
    {
        var area = Editor.TextArea;
        area.SelectionBrush = (Brush)FindResource("DD.Brush.Editor.Selection");
        area.SelectionForeground = null;
        area.SelectionBorder = null;
        area.SelectionCornerRadius = 0;
        area.Caret.CaretBrush = (Brush)FindResource("DD.Brush.Editor.Text");
        Editor.Options.HighlightCurrentLine = true;
        Editor.TextArea.TextView.CurrentLineBackground = (Brush)FindResource("DD.Brush.Editor.CurrentLine");
        Editor.TextArea.TextView.CurrentLineBorder = null;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
    }
}
