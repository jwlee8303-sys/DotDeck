using System.Text.Json.Nodes;
using DotDeck.Core.Explorer;
using DotDeck.Core.Toml;

namespace DotDeck.Core.Security;

/// <summary>Presentation-only masking. Never write the returned text back to a document.</summary>
public static class SecretPresentation
{
    public static bool MaskByDefault { get; set; } = true;

    public static bool SensitivePath(IEnumerable<string> path) => path.Any(p =>
        SecretMasker.IsSecretName(p) || p.Equals("env", StringComparison.OrdinalIgnoreCase)
        || p.Equals("http_headers", StringComparison.OrdinalIgnoreCase));

    public static string MaskDocument(string path, string text)
    {
        var all = SecretFilePolicy.IsSecret(Path.GetFileName(path));
        try
        {
            if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var node = JsonNode.Parse(text);
                var before = node?.ToJsonString();
                Redact(node, all);
                if (before == node?.ToJsonString()) return text;
                return node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? text;
            }
            if (Path.GetExtension(path).Equals(".toml", StringComparison.OrdinalIgnoreCase))
            {
                var doc = TomlEditDocument.Parse(System.Text.Encoding.UTF8.GetBytes(text));
                var entries = doc.Entries.Where(e => all || SensitivePath(e.Path)).OrderByDescending(e => e.Path.Count).ToList();
                foreach (var entry in entries)
                {
                    if (entry.Literal.Length > 2) SecretMasker.RegisterSecret(DecodeLiteral(entry.Literal));
                    doc.SetString(entry.Path, SecretMasker.Mask);
                }
                return System.Text.Encoding.UTF8.GetString(doc.ToBytes());
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or TomlEditException or ArgumentException)
        {
            // Invalid secret documents can still be revealed and edited; never expose raw fallback content.
            return all ? SecretMasker.Mask : SecretMasker.MaskText(text);
        }
        return all ? SecretMasker.Mask : SecretMasker.MaskText(text);
    }

    private static string DecodeLiteral(string literal)
    {
        var syntax = Tomlyn.Parsing.SyntaxParser.Parse("value = " + literal);
        return syntax.KeyValues.First().Value is Tomlyn.Syntax.StringValueSyntax value ? value.Value ?? literal : literal;
    }

    private static void Redact(JsonNode? node, bool sensitive)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                var mask = sensitive || SensitivePath([key]);
                if (obj[key] is JsonValue value && mask)
                {
                    SecretMasker.RegisterSecret(value.ToString());
                    obj[key] = SecretMasker.Mask;
                }
                else Redact(obj[key], mask);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value && sensitive)
                {
                    SecretMasker.RegisterSecret(value.ToString());
                    array[i] = SecretMasker.Mask;
                }
                else Redact(array[i], sensitive);
            }
        }
    }
}
