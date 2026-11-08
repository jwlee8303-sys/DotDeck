using DotDeck.Core.Catalogs;

namespace DotDeck.Core.Codex;

public sealed record ConfigChoice(string Value, string Description);
public sealed record ConfigField(string Key, string Label, string Description, string Default, IReadOnlyList<ConfigChoice> Choices);
public sealed class ConfigFormCatalog
{
    public const string Resource = "Codex/config-form.json";
    public int SchemaVersion { get; init; }
    public IReadOnlyList<ConfigField> Fields { get; init; } = [];
    public static ConfigFormCatalog BuiltIn => Parse(CatalogResources.ReadBuiltIn(Resource));
    public static ConfigFormCatalog Parse(string json)
    {
        var catalog = CatalogResources.Deserialize<ConfigFormCatalog>(json);
        if (catalog.SchemaVersion != 1 || catalog.Fields is null || catalog.Fields.Any(f => f is null || string.IsNullOrEmpty(f.Key)
            || f.Label is null || f.Description is null || f.Default is null || f.Choices is null || f.Choices.Any(c => c is null || c.Value is null || c.Description is null))
            || catalog.Fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() != catalog.Fields.Count)
            throw new InvalidDataException("Invalid Codex form catalog.");
        return catalog;
    }
}
