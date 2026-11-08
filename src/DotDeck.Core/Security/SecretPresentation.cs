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

        // A secret file may be a link alias ("notes.txt" -> auth.json / .env): judge its format by content, not name.
        if ((IsJson(path) || all) && TryCollectJson(text, all)) return;
        if ((IsToml(path) || all) && TryCollectToml(text, all)) return;
        if (all) CollectLines(text);
    }

    // Values shorter than this are not registered: masking "1" or "on" everywhere would make logs unreadable.
    private const int MinCollectedLength = 4;

    private static bool TryCollectJson(string text, bool all)
    {
        try
        {
            Collect(JsonNode.Parse(text), all);
            return true;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryCollectToml(string text, bool all)
    {
        try
        {
            var doc = TomlEditDocument.Parse(System.Text.Encoding.UTF8.GetBytes(text));
            foreach (var entry in doc.Entries.Where(e => all || SensitivePath(e.Path)))
            {
                if (entry.Literal.Length > 2) Register(DecodeLiteral(entry.Literal));
            }

            return true;
        }
        catch (Exception ex) when (ex is TomlEditException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Unstructured secret files (.env, keys): each assignment value, or each whole line for key material.</summary>
    private static void CollectLines(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("-----", StringComparison.Ordinal)) continue;
            var assignment = s_assignment.Match(line);
            if (!assignment.Success)
            {
                Register(line);
                continue;
            }

            foreach (var value in EnvValues(assignment.Groups["value"].Value)) Register(value);
        }
    }

    /// <summary>
    /// dotenv value: a quoted value ends at its closing quote (in double quotes a backslash escapes the next character);
    /// an unquoted one at " #" (inline comment). Double-quoted values yield both the raw and the unescaped form.
    /// </summary>
    private static IEnumerable<string> EnvValues(string raw)
    {
        var value = raw.Trim();
        if (value.Length > 0 && value[0] is '"' or '\'')
        {
            var quote = value[0];
            var close = -1;
            for (var i = 1; i < value.Length; i++)
            {
                if (quote == '"' && value[i] == '\\')
                {
                    i++;
                    continue;
                }

                if (value[i] == quote)
                {
                    close = i;
                    break;
                }
            }

            var inner = close > 0 ? value[1..close] : value[1..];
            yield return inner;
            if (quote == '"' && inner.Contains('\\', StringComparison.Ordinal)) yield return Unescape(inner);
            yield break;
        }

        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        yield return (comment >= 0 ? value[..comment] : value).TrimEnd();
    }

    private static string Unescape(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
                builder.Append(value[i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var c => c });
            }
            else
            {
                builder.Append(value[i]);
            }
        }

        return builder.ToString();
    }

    // NAME=value / export NAME=value / name: value. Key material lines (base64, may end with '=') do not match.
    private static readonly System.Text.RegularExpressions.Regex s_assignment = new(
        @"^(?:export\s+)?[A-Za-z_][\w.\-]*\s*[=:]\s*(?<value>.+)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static void Register(string value)
    {
        if (value.Length >= MinCollectedLength) SecretMasker.RegisterSecret(value);
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
                if (node is JsonValue && all) return text.Length == 0 ? text : SecretMasker.Mask;
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
        // A secret document may be a bare scalar ("token").
        if (node is JsonValue scalar && sensitive)
        {
            Register(scalar.ToString());
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var (key, child) in obj)
            {
                var mask = sensitive || SensitivePath([key]);
                if (child is JsonValue value && mask) Register(value.ToString());
                else Collect(child, mask);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is JsonValue value && sensitive) Register(value.ToString());
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
