using System.IO;
using System.Text.RegularExpressions;

namespace DotDeck.App.Tests;

/// <summary>
/// Colors belong to the theme dictionaries (SPEC §5): XAML and C# outside <c>Themes/</c> must reference
/// <c>DD.*</c> resource keys with DynamicResource so the light theme can replace every color.
/// </summary>
public sealed partial class ThemeColorTests
{
    // Files allowed to contain literal colors, with the reason.
    private static readonly Dictionary<string, string> s_allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        // Project label colors are user data stored in projects.json (the user picks one), not theme colors.
        [@"ViewModels\Dialogs\ProjectDialogViewModel.cs"] = "project label palette (user data)",
    };

    [GeneratedRegex(@"(?<![&\w])#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3,4})\b(?!;)")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"\b(?:Background|Foreground|BorderBrush|Fill|Stroke|Color|CaretBrush|SelectionBrush|OpacityMask)=""(?!\{)(?!Transparent"")[A-Za-z]+""")]
    private static partial Regex NamedColorAttribute();

    [GeneratedRegex(@"\b(?:Brushes|Colors|SystemColors)\.(?!Transparent\b)[A-Z]\w*|\bColor\.From(?:Rgb|Argb|ScRgb)\b|\bnew\s+SolidColorBrush\s*\(|\bColorConverter\.ConvertFromString\b|\bBrushConverter\b")]
    private static partial Regex CodeColor();

    [Fact]
    public void No_hard_coded_colors_outside_the_theme_dictionaries()
    {
        var app = Path.Combine(RepositoryRoot(), "src", "DotDeck.App");
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(app, file);
            if (relative.StartsWith(@"Themes\", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith(@"bin\", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith(@"obj\", StringComparison.OrdinalIgnoreCase)
                || s_allowed.ContainsKey(relative))
            {
                continue;
            }

            var xaml = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
            if (!xaml && !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;

                // A line may opt out with a stated reason, e.g. converting a theme resource color for another API.
                if (line.Contains("theme-color-ok:", StringComparison.Ordinal)) continue;
                var hit = HexColor().Match(line) is { Success: true } hex ? hex.Value
                    : xaml && NamedColorAttribute().Match(line) is { Success: true } named ? named.Value
                    : !xaml && CodeColor().Match(line) is { Success: true } code ? code.Value
                    : null;
                if (hit is not null) violations.Add($"{relative}:{i + 1}: {hit}");
            }
        }

        Assert.True(violations.Count == 0, "Use DD.* theme resource keys instead of literal colors:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Theme_palettes_define_the_same_keys()
    {
        var themes = Path.Combine(RepositoryRoot(), "src", "DotDeck.App", "Themes");
        static string[] Keys(string file) => [.. KeyRegex().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal)];
        Assert.Equal(Keys(Path.Combine(themes, "Dark.xaml")), Keys(Path.Combine(themes, "Light.xaml")));
    }

    [Fact]
    public void Control_styles_reference_colors_only_through_dynamic_resources()
    {
        var controls = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "DotDeck.App", "Themes", "Controls.xaml"));
        Assert.DoesNotMatch(HexColor(), controls);
        Assert.DoesNotMatch(NamedColorAttribute(), controls);
        Assert.DoesNotContain("StaticResource DD.Brush", controls, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_dialog_window_takes_background_and_text_from_the_theme()
    {
        var dialogs = Path.Combine(RepositoryRoot(), "src", "DotDeck.App", "Views", "Dialogs");
        foreach (var file in Directory.EnumerateFiles(dialogs, "*.xaml"))
        {
            var text = File.ReadAllText(file);
            var window = text[..text.IndexOf('>', text.IndexOf("<Window", StringComparison.Ordinal))];
            Assert.Contains("Background=\"{DynamicResource DD.Brush.", window, StringComparison.Ordinal);
            Assert.Contains("Foreground=\"{DynamicResource DD.Brush.", window, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"x:Key=""([^""]+)""")]
    private static partial Regex KeyRegex();

    internal static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DotDeck.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("Repository root (DotDeck.slnx) not found.");
    }
}
