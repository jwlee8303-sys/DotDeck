using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;

namespace DotDeck.App.Tests;

/// <summary>The AvalonEdit behavior the soft wrap toggle relies on, checked on a real editor control.</summary>
public sealed class WordWrapEditorTests
{
    private static Task RunOnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name)
            {
                return match;
            }

            if (Find<T>(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [Fact]
    public Task Wrap_folds_long_lines_disables_horizontal_scrolling_and_keeps_logical_line_numbers() => RunOnSta(() =>
    {
        var text = "short\r\n    - " + string.Join(' ', Enumerable.Repeat("word", 200)) + "\r\nlast";
        var editor = new TextEditor
        {
            Document = new TextDocument(text),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,  // as in TextEditorView
            Width = 300,
            Height = 400,
        };
        editor.Options.InheritWordWrapIndentation = true;
        var window = new Window { Content = editor, Width = 320, Height = 420, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -10000 };
        try
        {
            window.Show();
            editor.UpdateLayout();
            var scroller = Find<ScrollViewer>(editor, "PART_ScrollViewer");
            Assert.NotNull(scroller);
            Assert.Equal(ScrollBarVisibility.Auto, scroller.HorizontalScrollBarVisibility);
            var view = editor.TextArea.TextView;
            view.EnsureVisualLines();
            var longLine = editor.Document.GetLineByNumber(2);
            Assert.Single(view.GetOrConstructVisualLine(longLine).TextLines);

            editor.WordWrap = true;
            editor.UpdateLayout();
            view.EnsureVisualLines();

            Assert.Equal(ScrollBarVisibility.Disabled, scroller.HorizontalScrollBarVisibility);
            Assert.True(view.GetOrConstructVisualLine(longLine).TextLines.Count > 1);

            // The caret at the end of the wrapped line is still on file line 2, and the text did not change.
            editor.CaretOffset = longLine.EndOffset;
            Assert.Equal(2, editor.TextArea.Caret.Line);
            editor.CaretOffset = editor.Document.GetLineByNumber(3).Offset;
            Assert.Equal(3, editor.TextArea.Caret.Line);
            Assert.Equal(text, editor.Document.Text);
            Assert.False(editor.Document.UndoStack.CanUndo);

            editor.WordWrap = false;
            editor.UpdateLayout();
            Assert.Equal(ScrollBarVisibility.Auto, scroller.HorizontalScrollBarVisibility);
        }
        finally
        {
            window.Close();
        }
    });
}
