using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DotDeck.App.ViewModels.Documents;

namespace DotDeck.App.Views.Editor;

/// <summary>Editor for an untitled document; takes keyboard focus when its tab becomes active (Ctrl+N, then type).</summary>
public partial class UntitledEditorView : UserControl
{
    public UntitledEditorView()
    {
        InitializeComponent();
        Editor.TextArea.SelectionBrush = (Brush)FindResource("DD.Brush.Editor.Selection");
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.Caret.CaretBrush = (Brush)FindResource("DD.Brush.Editor.Text");
        Editor.Options.InheritWordWrapIndentation = true;
        Loaded += (_, _) => FocusWhenActive();
        DataContextChanged += (_, _) => FocusWhenActive();
    }

    private void FocusWhenActive() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsVisible && DataContext is ScratchDocumentViewModel { IsActive: true })
            {
                Editor.TextArea.Focus();
            }
        });
}
