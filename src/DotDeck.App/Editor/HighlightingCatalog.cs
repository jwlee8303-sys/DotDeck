using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace DotDeck.App.Editor;

/// <summary>
/// Dark-theme syntax definitions embedded as <c>Editor/Highlighting/*.Dark.xshd</c>. AvalonEdit's built-in
/// definitions are tuned for light backgrounds, so only these are used.
/// </summary>
public static class HighlightingCatalog
{
    private static readonly string[] s_names = ["Json", "Markdown", "Toml", "Yaml", "Xml", "Ini"];

    // Extension-less names that are still config files.
    private static readonly Dictionary<string, string> s_fileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [".editorconfig"] = "INI",
        [".gitconfig"] = "INI",
        [".npmrc"] = "INI",
        ["config"] = "INI",
    };

    private static readonly Lazy<Dictionary<string, IHighlightingDefinition>> s_byExtension = new(Load);

    public static IHighlightingDefinition? ForPath(string path)
    {
        var fileName = System.IO.Path.GetFileName(path);
        var map = s_byExtension.Value;
        if (s_fileNames.TryGetValue(fileName, out var name))
        {
            return map.Values.FirstOrDefault(d => d.Name == name);
        }

        return map.GetValueOrDefault(System.IO.Path.GetExtension(path));
    }

    private static Dictionary<string, IHighlightingDefinition> Load()
    {
        var result = new Dictionary<string, IHighlightingDefinition>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(HighlightingCatalog).Assembly;
        foreach (var name in s_names)
        {
            // A broken definition only loses its colouring; it must never stop a file from opening.
            try
            {
                using var stream = assembly.GetManifestResourceStream($"DotDeck.App.Editor.Highlighting.{name}.Dark.xshd")
                    ?? throw new InvalidOperationException($"Missing highlighting resource {name}.");
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                var xshd = HighlightingLoader.LoadXshd(reader);
                var definition = HighlightingLoader.Load(xshd, HighlightingManager.Instance);
                foreach (var extension in xshd.Extensions)
                {
                    result[extension] = definition;
                }
            }
            catch (Exception ex) when (ex is HighlightingDefinitionInvalidException or XmlException or InvalidOperationException)
            {
                LoadErrors.Add($"{name}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>Definitions that failed to load (shown nowhere yet; kept for diagnostics and tests).</summary>
    public static List<string> LoadErrors { get; } = [];

    /// <summary>Forces loading every definition and returns the failures.</summary>
    public static IReadOnlyList<string> Validate()
    {
        _ = s_byExtension.Value;
        return LoadErrors;
    }
}
