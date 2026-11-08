using System.Text.Json.Nodes;
using DotDeck.Core.Catalogs;
using DotDeck.Core.Roles;

namespace DotDeck.Core.Editors;

/// <summary>A group of settings keys that only apply at some levels (<c>Catalogs/Claude/settings-keys.json</c>).</summary>
public sealed class SettingsKeyGroup
{
    public string Id { get; init; } = string.Empty;

    public string Severity { get; init; } = "warning";

    /// <summary>Levels where the keys have no effect (user, project, local, managed).</summary>
    public IReadOnlyList<string>? IgnoredIn { get; init; }

    /// <summary>Levels where the message applies (for notes rather than "ignored" warnings).</summary>
    public IReadOnlyList<string>? AppliesIn { get; init; }

    /// <summary>Only these values trigger the message (null: any value).</summary>
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>Text with <c>{key}</c> and <c>{value}</c> placeholders.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Dotted key paths, e.g. <c>permissions.defaultMode</c>.</summary>
    public IReadOnlyList<string> Keys { get; init; } = [];
}

public sealed class SettingsKeyCatalog
{
    public const string ClaudeResource = "Claude/settings-keys.json";

    public int SchemaVersion { get; init; }

    public IReadOnlyList<CatalogSource> Sources { get; init; } = [];

    public IReadOnlyList<SettingsKeyGroup> Groups { get; init; } = [];

    public static SettingsKeyCatalog Parse(string json)
    {
        var catalog = CatalogResources.Deserialize<SettingsKeyCatalog>(json);
        if (catalog.SchemaVersion != 1 || catalog.Groups is null
            || catalog.Groups.Any(g => g is null || g.Keys is null || g.Message is null || g.Keys.Any(k => string.IsNullOrEmpty(k))))
        {
            throw new InvalidDataException("Unsupported settings-keys catalog.");
        }

        return catalog;
    }

    public static SettingsKeyCatalog LoadClaude() => Parse(CatalogResources.ReadBuiltIn(ClaudeResource));
}

/// <summary>Warns about settings keys that are ignored (or behave differently) at the file's level.</summary>
public static class SettingsScopeLint
{
    /// <summary>Catalog level name for a settings file scope.</summary>
    public static string LevelOf(ConfigScope scope) => scope switch
    {
        ConfigScope.Global => "user",
        ConfigScope.Project => "project",
        ConfigScope.Local => "local",
        ConfigScope.Managed => "managed",
        _ => "project",
    };

    public static IReadOnlyList<ValidationIssue> Check(JsonObject root, ConfigScope scope, SettingsKeyCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(catalog);
        var level = LevelOf(scope);
        var issues = new List<ValidationIssue>();
        foreach (var group in catalog.Groups)
        {
            var applies = group.IgnoredIn?.Contains(level, StringComparer.Ordinal) == true
                || group.AppliesIn?.Contains(level, StringComparer.Ordinal) == true;
            if (!applies)
            {
                continue;
            }

            var severity = group.Severity switch
            {
                "info" => IssueSeverity.Info,
                "error" => IssueSeverity.Error,
                _ => IssueSeverity.Warning,
            };
            foreach (var key in group.Keys)
            {
                if (Find(root, key) is not { } node)
                {
                    continue;
                }

                var value = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                if (group.Values is { } values && (value is null || !values.Contains(value, StringComparer.Ordinal)))
                {
                    continue;
                }

                issues.Add(new ValidationIssue(severity,
                    group.Message.Replace("{key}", key, StringComparison.Ordinal).Replace("{value}", value ?? string.Empty, StringComparison.Ordinal)));
            }
        }

        return issues;
    }

    private static JsonNode? Find(JsonObject root, string dottedKey)
    {
        JsonNode? current = root;
        foreach (var part in dottedKey.Split('.'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(part, out current) || current is null)
            {
                return null;
            }
        }

        return current;
    }
}
