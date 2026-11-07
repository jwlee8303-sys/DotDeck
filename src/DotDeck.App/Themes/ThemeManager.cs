using System.Windows;
using AvalonDock;
using AvalonDock.Themes.VS;

namespace DotDeck.App.Themes;

public enum ThemeKind
{
    Dark,
}

/// <summary>
/// Applies the DotDeck theme. Application resources hold: [0] palette (swappable), [1] metrics, [2] control styles.
/// AvalonDock gets its own VS theme plus DotDeck overrides merged after it.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri s_dockOverrides = new("pack://application:,,,/DotDeck;component/Themes/DockOverrides.xaml");

    public static ThemeKind Current { get; private set; } = ThemeKind.Dark;

    public static void ApplyDocking(DockingManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        manager.Theme = Current switch
        {
            _ => new VS2022DarkTheme(),
        };

        AppendOverrides(manager);
    }

    /// <summary>
    /// AvalonDock floating windows merge the base theme into their own resources, so the overrides
    /// must be appended there as well (call when the floating window control is created).
    /// </summary>
    public static void ApplyFloating(FrameworkElement floatingWindow)
    {
        ArgumentNullException.ThrowIfNull(floatingWindow);
        AppendOverrides(floatingWindow);
    }

    // Re-append overrides so they come after the AvalonDock theme dictionary and win the lookup.
    private static void AppendOverrides(FrameworkElement element)
    {
        var merged = element.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d => d.Source == s_dockOverrides);
        if (existing is not null)
        {
            merged.Remove(existing);
        }

        merged.Add(new ResourceDictionary { Source = s_dockOverrides });
    }
}
