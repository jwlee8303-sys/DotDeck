using System.Text.Json;
using DotDeck.Core.Files;

namespace DotDeck.Core.Catalogs;

/// <summary>Where a catalog came from: the built-in copy, or a user file that replaces it.</summary>
public enum CatalogOrigin
{
    BuiltIn,
    Override,
}

/// <summary>A loaded catalog. <paramref name="Warning"/> explains why an override file was not used.</summary>
public sealed record LoadedCatalog<T>(T Value, CatalogOrigin Origin, string? Warning);

/// <summary>
/// Data catalogs (descriptions of Claude Code files, tools, hook events, settings keys) shipped as embedded JSON
/// under <c>Catalogs/&lt;product&gt;/&lt;name&gt;.json</c>. A file with the same relative path under the user's override
/// folder (<c>%APPDATA%\DotDeck\catalogs\</c>) replaces the built-in copy when it parses; otherwise the built-in
/// copy is used and a warning is returned.
/// </summary>
public static class CatalogResources
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Text of a built-in catalog, e.g. <c>"Claude/tools.json"</c>.</summary>
    public static string ReadBuiltIn(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        var name = "DotDeck.Core.Catalogs." + relativePath.Replace('/', '.').Replace('\\', '.');
        using var stream = CoreAssembly.Instance.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Built-in catalog not found: {relativePath}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Loads a catalog with <paramref name="parse"/> (which throws on invalid data). The override file wins when
    /// it exists and parses.
    /// </summary>
    public static LoadedCatalog<T> Load<T>(string relativePath, Func<string, T> parse, IFileStore? store = null, string? overrideDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(parse);
        string? warning = null;
        if (store is not null && !string.IsNullOrEmpty(overrideDirectory))
        {
            var path = Path.Combine(overrideDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (store.FileExists(path))
            {
                try
                {
                    return new LoadedCatalog<T>(parse(store.ReadAllText(path)), CatalogOrigin.Override, null);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    warning = $"카탈로그 파일을 쓸 수 없어 기본값을 사용합니다: {path} ({ex.GetType().Name})";
                }
            }
        }

        return new LoadedCatalog<T>(parse(ReadBuiltIn(relativePath)), CatalogOrigin.BuiltIn, warning);
    }

    /// <summary>Deserializes and rejects a null result.</summary>
    public static T Deserialize<T>(string json)
        where T : class =>
        JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidDataException("Catalog is empty.");
}

/// <summary>An official documentation page a catalog was checked against.</summary>
public sealed record CatalogSource(string Url, string Checked);
