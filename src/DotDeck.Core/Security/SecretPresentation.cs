using System.Text.Json.Nodes;
using DotDeck.Core.Explorer;
using DotDeck.Core.Toml;

namespace DotDeck.Core.Security;

/// <summary>
/// Screen masking of secret values. Values are shown in plain text unless the "화면 공유 모드" (screen share mode)
/// is on. Collecting known secret values for the log/output filter is separate and always runs.
/// Never write the returned text back to a document.
/// </summary>
public static class SecretPresentation
{
    private static bool s_screenShareMode;

    /// <summary>Raised with the new value after <see cref="ScreenShareMode"/> changes.</summary>
    public static event Action<bool>? ScreenShareModeChanged;

    /// <summary>When on, secret values are masked on screen (eye buttons reveal them one view at a time).</summary>
    public static bool ScreenShareMode
    {
        get => s_screenShareMode;
        set
        {
            if (s_screenShareMode == value) return;
            s_screenShareMode = value;
            ScreenShareModeChanged?.Invoke(value);
        }
    }

    public static bool SensitivePath(IEnumerable<string> path) => path.Any(p =>
        SecretMasker.IsSecretName(p) || p.Equals("env", StringComparison.OrdinalIgnoreCase)
        || p.Equals("http_headers", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Registers the secret values of a document with <see cref="SecretMasker"/> so logs, the output pane and error
    /// messages never show them. Independent of <see cref="ScreenShareMode"/>. Unparsable documents register nothing.
    /// </summary>
    public static void CollectSecrets(string path, string text, bool isSecretFile = false)
    {
        var all = isSecretFile || SecretFilePolicy.IsSecret(Path.GetFileName(path));
        try
        {
            if (IsJson(path))
            {
                Collect(JsonNode.Parse(text), all);
            }
            else if (IsToml(path))
            {
                var doc = TomlEditDocument.Parse(System.Text.Encoding.UTF8.GetBytes(text));
                foreach (var entry in doc.Entries.Where(e => all || SensitivePath(e.Path)))
                {
                    if (entry.Literal.Length > 2) SecretMasker.RegisterSecret(DecodeLiteral(entry.Literal));
                }
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or TomlEditException or ArgumentException or InvalidOperationException)
        {
            // Nothing structured to collect; MaskText still filters secret-looking assignments in logs.
        }
    }

    /// <summary>
    /// Screen share masking. Structured documents (JSON/TOML) mask by path; when they cannot be parsed the whole
    /// document is masked. Secret files (by name or link target) are masked completely.
    /// </summary>
    public static string MaskDocument(string path, string text, bool isSecretFile = false)
    {
        var all = isSecretFile || SecretFilePolicy.IsSecret(Path.GetFileName(path));
        try
        {
            if (IsJson(path))
            {
                var node = JsonNode.Parse(text);
                var before = node?.ToJsonString();
                Redact(node, all);
                if (before == node?.ToJsonString()) return text;
                return node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? text;
            }
            if (IsToml(path))
            {
                var doc = TomlEditDocument.Parse(System.Text.Encoding.UTF8.GetBytes(text));
                var entries = doc.Entries.Where(e => all || SensitivePath(e.Path)).OrderByDescending(e => e.Path.Count).ToList();
                foreach (var entry in entries)
                {
                    doc.SetString(entry.Path, SecretMasker.Mask);
                }
                return System.Text.Encoding.UTF8.GetString(doc.ToBytes());
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or TomlEditException or ArgumentException or InvalidOperationException)
        {
            // Simple rule while sharing the screen: an unparsable structured document is hidden completely.
            return text.Length == 0 ? text : SecretMasker.Mask;
        }
        return all ? (text.Length == 0 ? text : SecretMasker.Mask) : SecretMasker.MaskText(text);
    }

    private static bool IsJson(string path) => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);

    private static bool IsToml(string path) => Path.GetExtension(path).Equals(".toml", StringComparison.OrdinalIgnoreCase);

    private static string DecodeLiteral(string literal)
    {
        var syntax = Tomlyn.Parsing.SyntaxParser.Parse("value = " + literal);
        return syntax.KeyValues.First().Value is Tomlyn.Syntax.StringValueSyntax value ? value.Value ?? literal : literal;
    }

    private static void Collect(JsonNode? node, bool sensitive)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, child) in obj)
            {
                var mask = sensitive || SensitivePath([key]);
                if (child is JsonValue value && mask) SecretMasker.RegisterSecret(value.ToString());
                else Collect(child, mask);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is JsonValue value && sensitive) SecretMasker.RegisterSecret(value.ToString());
                else Collect(child, sensitive);
            }
        }
    }

    private static void Redact(JsonNode? node, bool sensitive)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                var mask = sensitive || SensitivePath([key]);
                if (obj[key] is JsonValue && mask) obj[key] = SecretMasker.Mask;
                else Redact(obj[key], mask);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue && sensitive) array[i] = SecretMasker.Mask;
                else Redact(array[i], sensitive);
            }
        }
    }
}
