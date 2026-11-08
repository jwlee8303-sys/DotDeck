using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DotDeck.App.ViewModels.Editor;
using DotDeck.App.Views.Editor;
using DotDeck.Core.Editors;
using DotDeck.Core.Hooks;
using DotDeck.Core.Documents;
using DotDeck.App.ViewModels.Documents;

namespace DotDeck.App.Tests;

public sealed class FormXamlTests
{
    [Fact]
    public async Task Shared_hook_table_and_settings_template_load_and_bind_on_sta()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Application? application = null;
            try
            {
                // Plain WPF application: no DotDeck startup, user settings or real home access.
                application = new Application();
                foreach (var file in new[] { "Dark.xaml", "Metrics.xaml", "Controls.xaml" })
                {
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"/DotDeck;component/Themes/{file}", UriKind.Relative),
                    });
                }

                var table = new HookTableViewModel { GetEvents = () => HookEventCatalog.BuiltIn };
                table.Load(new ClaudeSettingsHookStore(ClaudeSettingsDocument.TryParse("{}", out _)!));
                table.AddHookCommand.Execute("PreToolUse");
                var view = new HookTable { DataContext = table };
                view.Measure(new Size(1000, 800));
                view.Arrange(new Rect(0, 0, 1000, 800));
                view.UpdateLayout();
                var items = Descendants(view).OfType<ItemsControl>().Single();
                Assert.Single(items.Items);
                Assert.Same(table.Rows[0], items.Items[0]);
                var run = Descendants(view).OfType<Button>().Single(b => b.Command == table.RunCommand);
                Assert.Same(table.Rows[0], run.CommandParameter);

                var templates = new FormTemplates();
                var template = Assert.IsType<DataTemplate>(templates[new DataTemplateKey(typeof(SettingsFormViewModel))]);
                var settingsView = Assert.IsType<ScrollViewer>(template.LoadContent());
                var settings = new SettingsFormViewModel(_ => { });
                Assert.Null(settings.Load("{}"));
                settingsView.DataContext = settings;
                settingsView.Measure(new Size(1000, 800));
                settingsView.Arrange(new Rect(0, 0, 1000, 800));
                settingsView.UpdateLayout();
                Assert.Same(settings.HookTable, Descendants(settingsView).OfType<HookTable>().Single().DataContext);
                var original = "name = '한글'";
                var snapshot = TextFileReader.Decode(@"C:\fake\config.toml", TextEncodings.Encode(original, TextEncodingKind.Cp949), DateTime.UtcNow);
                var buffer = new DocumentBuffer(snapshot);
                var document = new TextDocumentViewModel(buffer, 1, false, new TestHost(), _ => { }, kind: EditorKind.CodexConfig);
                var editorView = new TextEditorView { DataContext = document };
                editorView.Measure(new Size(1000, 800));
                editorView.Arrange(new Rect(0, 0, 1000, 800));
                editorView.UpdateLayout();
                Assert.True(Assert.IsType<ICSharpCode.AvalonEdit.TextEditor>(editorView.FindName("Editor")).IsReadOnly);
                Assert.Contains("UTF-8", buffer.ReadOnlyReason, StringComparison.Ordinal);
                Assert.False(document.HasForm);
                document.Find.Pattern = "name";
                document.Find.Replacement = "changed";
                document.Find.ReplaceAllCommand.Execute(null);
                Assert.Equal(original, buffer.Document.Text);
                document.Detach();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                application?.Shutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class TestHost : ITextDocumentHost
    {
        public Task SaveAsync(TextDocumentViewModel document) => Task.CompletedTask;
        public void OpenNewView(TextDocumentViewModel document) { }
        public void SetEditorKind(TextDocumentViewModel document, EditorKind? kind) { }
        public void ShowFormHelp(DotDeck.App.ViewModels.Tools.RoleDescription? help) { }
    }
}
