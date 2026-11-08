using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DotDeck.App.ViewModels.Editor;
using DotDeck.Core.Editors;

namespace DotDeck.App.Views.Editor;

/// <summary>Form templates; the ▾ buttons open a menu of suggestions (Tag) for a row or a command.</summary>
public partial class FormTemplates : ResourceDictionary
{
    public FormTemplates() => InitializeComponent();

    /// <summary>Tag holds strings or <see cref="ChoiceItem"/>s (grouped, with descriptions as tooltips).</summary>
    private void OnDropButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: IEnumerable items } button)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, MaxHeight = 480 };
        string? group = null;
        foreach (var item in items)
        {
            var choice = item switch
            {
                ChoiceItem c => c,
                string text => new ChoiceItem(text, text),
                _ => null,
            };
            if (choice is null)
            {
                continue;
            }

            if (choice.Group is { } g && g != group)
            {
                AddGroupHeader(menu, g);
                group = g;
            }

            var menuItem = new MenuItem { Header = new TextBlock { Text = choice.Label }, ToolTip = Tip(choice.Description) };
            menuItem.Click += (_, _) => Choose(button.CommandParameter, choice.Value);
            menu.Items.Add(menuItem);
        }

        menu.IsOpen = true;
    }

    /// <summary>Matcher ☰: check boxes for tools, MCP servers or the event's values; the menu stays open while checking.</summary>
    private void OnMatcherClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HookRow row } button)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, MaxHeight = 480 };
        if (HookMatcher.Parse(row.Matcher, HookMatcher.SyntaxOf(row.EventInfo)).Mode == HookMatcherMode.Regex)
        {
            menu.Items.Add(new MenuItem
            {
                Header = new TextBlock { Text = "현재 값은 정규식입니다. 항목을 고르면 목록으로 바뀝니다.", TextWrapping = TextWrapping.Wrap, MaxWidth = 320 },
                IsEnabled = false,
            });
        }

        var checks = new List<(MenuItem Item, string Value)>();
        string? group = null;
        foreach (var choice in row.MatcherChoices())
        {
            if (choice.Group is { } g && g != group)
            {
                AddGroupHeader(menu, g);
                group = g;
            }

            var menuItem = new MenuItem
            {
                Header = new TextBlock { Text = choice.Label },
                ToolTip = Tip(choice.Description),
                IsCheckable = true,
                IsChecked = choice.IsChecked,
                StaysOpenOnClick = true,
            };
            var value = choice.Value;
            menuItem.Click += (_, _) =>
            {
                row.ToggleMatcher(value, menuItem.IsChecked);

                // Setting IsChecked from code does not raise Click: refresh every box from the new matcher.
                var states = row.MatcherChoices().GroupBy(c => c.Value, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Any(c => c.IsChecked), StringComparer.Ordinal);
                foreach (var (item, v) in checks)
                {
                    item.IsChecked = states.TryGetValue(v, out var on) && on;
                }
            };
            checks.Add((menuItem, value));
            menu.Items.Add(menuItem);
        }

        menu.IsOpen = true;
    }

    /// <summary>Focus inside a row selects it, so the properties pane shows its help.</summary>
    private void OnRowFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FormRow row } element && FindForm(element) is { } form)
        {
            form.SelectedRow = row;
        }
    }

    private static SettingsFormViewModel? FindForm(DependencyObject element)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: SettingsFormViewModel form })
            {
                return form;
            }
        }

        return null;
    }

    private static void AddGroupHeader(ContextMenu menu, string group)
    {
        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(new MenuItem { Header = new TextBlock { Text = group, FontWeight = FontWeights.SemiBold }, IsEnabled = false });
    }

    private static object? Tip(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 };

    private static void Choose(object? target, string value)
    {
        if (target is ICommand command && command.CanExecute(value))
        {
            command.Execute(value);
        }
    }
}
