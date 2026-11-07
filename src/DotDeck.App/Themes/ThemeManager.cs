using System.Windows;
using AvalonDock;
using AvalonDock.Themes;
using AvalonDock.Themes.VS;
using DotDeck.App.Infrastructure.Windows;

namespace DotDeck.App.Themes;

public enum ThemeKind
{
    Dark,
    Light,
}

/// <summary>
/// Applies the DotDeck theme. Application resources hold: [0] palette (swappable), [1] metrics, [2] control styles.
/// AvalonDock gets its own VS theme plus DotDeck overrides merged after it.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri s_dockOverrides = new("pack://application:,,,/DotDeck;component/Themes/DockOverrides.xaml");

    public static ThemeKind Current { get; private set; } = ThemeKind.Dark;

    /// <summary>Raised after the palette changed (docking managers and title bars follow).</summary>
    public static event EventHandler? Changed;

    /// <summary>Swaps the palette dictionary; every brush is a DynamicResource, so open views update in place.</summary>
    public static void Apply(ThemeKind kind)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (kind == Current && app.Resources.MergedDictionaries.Count > 0)
        {
            return;
        }

        Current = kind;
        app.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/DotDeck;component/Themes/{kind}.xaml"),
        };
        foreach (Window window in app.Windows)
        {
            DarkTitleBar.Attach(window);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Sets the editor font size (overrides the Metrics value; editors bind it dynamically).</summary>
    public static void ApplyEditorFontSize(double size)
    {
        if (Application.Current is { } app)
        {
            app.Resources["DD.FontSize.Editor"] = size;
        }
    }

    public static void ApplyDocking(DockingManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        manager.Theme = Current switch
        {
            ThemeKind.Light => new VS2022LightTheme(),
            _ => (Theme)new VS2022DarkTheme(),
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
