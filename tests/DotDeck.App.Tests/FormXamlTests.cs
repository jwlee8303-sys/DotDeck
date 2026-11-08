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
                var toml = new TomlFormViewModel(_ => { });
                Assert.Null(toml.Load("inline = { x = 1 }\n"));
                toml.Selected = toml.Rows.Single(r => r.Label == "inline.x");
                var tomlTemplate = Assert.IsType<DataTemplate>(templates[new DataTemplateKey(typeof(TomlFormViewModel))]);
                var tomlView = Assert.IsType<ScrollViewer>(tomlTemplate.LoadContent());
                tomlView.DataContext = toml;
                tomlView.Measure(new Size(1000, 800));
                tomlView.Arrange(new Rect(0, 0, 1000, 800));
                tomlView.UpdateLayout();
                Assert.False(Descendants(tomlView).OfType<Button>().Single(b => b.Command == toml.DeleteKeyCommand).IsEnabled);
                Assert.False(Descendants(tomlView).OfType<Button>().Single(b => b.Command == toml.AddKeyCommand).IsEnabled);
                var configSource = "# keep\nmodel = 'before'\nextra = [1, 'a']\n[mcp_servers.demo.env]\nAPI_KEY = 'fake-ui-key-2471'\n";
                var configBuffer = new DocumentBuffer(TextFileReader.Decode(@"C:\fake\config.toml", System.Text.Encoding.UTF8.GetBytes(configSource), DateTime.UtcNow));
                var configDocument = new TextDocumentViewModel(configBuffer, 1, false, new TestHost(), _ => { }, kind: EditorKind.CodexConfig);
                configDocument.TryShowForm();
                var configForm = Assert.IsType<CodexConfigFormViewModel>(configDocument.Form);
                var configView = new CodexConfigFormView { DataContext = configForm };
                configView.Measure(new Size(1100, 800));
                configView.Arrange(new Rect(0, 0, 1100, 800));
                configView.UpdateLayout();
                Assert.Equal(6, Descendants(configView).OfType<TabControl>().Single().Items.Count);

                // Dark theme coverage: every form control has a DotDeck style and no light system surface remains.
                foreach (var type in new[] { typeof(TabControl), typeof(TabItem), typeof(ComboBox), typeof(ComboBoxItem), typeof(DataGrid),
                    typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), typeof(DataGridRow), typeof(DataGridCell), typeof(TreeView), typeof(TreeViewItem),
                    typeof(Expander), typeof(PasswordBox), typeof(System.Windows.Controls.Primitives.ToggleButton), typeof(GroupBox),
                    typeof(RadioButton), typeof(TextBox), typeof(CheckBox), typeof(ListBox), typeof(Button), typeof(ToolTip), typeof(ContextMenu) })
                {
                    Assert.IsType<Style>(application.FindResource(type));
                }

                var tabs = Descendants(configView).OfType<TabControl>().Single();
                for (var index = 0; index < tabs.Items.Count; index++)
                {
                    tabs.SelectedIndex = index;
                    configView.Measure(new Size(1100, 800)); configView.Arrange(new Rect(0, 0, 1100, 800)); configView.UpdateLayout();
                    AssertDarkSurfaces(configView, $"config.toml tab {index}");
                    foreach (var combo in Descendants(configView).OfType<ComboBox>())
                    {
                        var dropDown = Assert.IsType<System.Windows.Controls.Border>(combo.Template.FindName("DropDown", combo));
                        AssertDark(dropDown.Background, "combo drop-down");
                    }
                }

                Assert.NotEmpty(Descendants(configView).OfType<TreeViewItem>());
                tabs.SelectedIndex = 3;
                configView.UpdateLayout();
                Assert.NotEmpty(Descendants(configView).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>());
                AssertDarkSurfaces(configView, "config.toml MCP grid");
                AssertDarkSurfaces(settingsView, "settings.json form");
                AssertDarkSurfaces(view, "hook table");
                foreach (var kind in new[] { EditorKind.Agent, EditorKind.Skill })
                {
                    var frontmatter = new FrontmatterFormViewModel(kind, _ => { }, ["Read", "Grep"]);
                    Assert.Null(frontmatter.Load("---\nname: demo\ndescription: d\ntools: Read\n---\nbody"));
                    var frontmatterTemplate = Assert.IsType<DataTemplate>(templates[new DataTemplateKey(typeof(FrontmatterFormViewModel))]);
                    var frontmatterView = Assert.IsAssignableFrom<FrameworkElement>(frontmatterTemplate.LoadContent());
                    frontmatterView.DataContext = frontmatter;
                    frontmatterView.Measure(new Size(1000, 800));
                    frontmatterView.Arrange(new Rect(0, 0, 1000, 800));
                    frontmatterView.UpdateLayout();
                    AssertDarkSurfaces(frontmatterView, $"{kind} form");
                }

                tabs.SelectedIndex = 0;
                Descendants(configView).OfType<TabControl>().Single().SelectedIndex = 5;
                configForm.Advanced.Selected = configForm.Advanced.Roots.Single().Children.Single(n => n.Label == "extra");
                configView.Measure(new Size(1100, 800)); configView.UpdateLayout();
                Assert.True(configForm.Advanced.IsArray);
                configForm.Models.First().Value = "after";
                Assert.Contains("model = 'after'", configBuffer.Document.Text, StringComparison.Ordinal);
                configBuffer.Document.UndoStack.Undo();
                Assert.Equal(configSource, configBuffer.Document.Text);
                configDocument.Detach();
                var original = "name = '한글'";
                var snapshot = TextFileReader.Decode(@"C:\fake\config.toml", TextEncodings.Encode(original, TextEncodingKind.Cp949), DateTime.UtcNow);
                var buffer = new DocumentBuffer(snapshot);
                var document = new TextDocumentViewModel(buffer, 1, false, new TestHost(), _ => { }, kind: EditorKind.CodexConfig);
                var editorView = new TextEditorView { DataContext = document };
                editorView.Measure(new Size(1000, 800));
                editorView.Arrange(new Rect(0, 0, 1000, 800));
                editorView.UpdateLayout();
                Assert.True(Assert.IsType<ICSharpCode.AvalonEdit.TextEditor>(editorView.FindName("Editor")).IsReadOnly);
                AssertDarkSurfaces(editorView, "text editor view bar");
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

    private static void AssertDarkSurfaces(DependencyObject root, string where)
    {
        foreach (var element in new[] { root }.Concat(Descendants(root)))
        {
            var background = element switch
            {
                Control control => control.Background,
                Border border => border.Background,
                Panel panel => panel.Background,
                _ => null,
            };
            AssertDark(background, $"{where}: {element.GetType().Name} {(element as FrameworkElement)?.Name} content={(element as ContentControl)?.Content} templatedParent={(element as FrameworkElement)?.TemplatedParent?.GetType().Name}");
        }
    }

    private static void AssertDark(Brush? brush, string where)
    {
        if (brush is not SolidColorBrush { Color: var c } || c.A == 0) return;
        var luminance = ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255;
        Assert.True(luminance < 0.5, $"Light background {c} at {where}");
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
