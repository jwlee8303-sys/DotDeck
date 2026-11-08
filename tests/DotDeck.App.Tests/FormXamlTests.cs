using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DotDeck.App.ViewModels.Editor;
using DotDeck.App.Views.Editor;
using DotDeck.Core.Editors;
using DotDeck.Core.Hooks;

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
}
