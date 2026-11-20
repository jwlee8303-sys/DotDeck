using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotDeck.IconGen;

/// <summary>One entry of <c>file-icon-exclude.json</c>.</summary>
/// <param name="Key">Material icon key whose artwork is not shipped (a third-party logo).</param>
/// <param name="Reason">Why (kept for reviewers).</param>
/// <param name="ReplaceWith">Optional existing key whose artwork is used instead; by default a generic file/folder in the icon's main color.</param>
internal sealed record ExcludeRule(string Key, string Reason, string? ReplaceWith);

internal sealed record GenerateOptions(
    string IconsDir,
    string ManifestPath,
    string? ExcludePath,
    string? ExtraDir,
    string OutputDir);

internal sealed record IconFailure(string Key, string Reason);

internal sealed record GenerateResult(
    int Converted,
    IReadOnlyList<IconFailure> Failed,
    IReadOnlyList<ExcludeRule> Excluded,
    IReadOnlyList<string> Extra,
    int Removed);

/// <summary>
/// Writes <c>&lt;key&gt;.xaml</c> for every icon of the manifest (plus DotDeck's own SVGs). Output is deterministic:
/// same inputs give byte-identical files, and stale <c>.xaml</c> files in the output folder are removed.
/// </summary>
internal static partial class IconGenerator
{
    /// <summary>Material's generic icons, used (recolored) for excluded and unconvertible icons.</summary>
    public const string FileKey = "file";
    public const string FolderKey = "folder";
    public const string FolderOpenKey = "folder-open";

    private const string FallbackColor = "#90A4AE";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SafeName();

    /// <summary>Windows device names, which cannot be file names in any case or with any extension.</summary>
    [GeneratedRegex(@"^(con|prn|aux|nul|com[0-9]|lpt[0-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();

    /// <summary>A key usable as a file name; keys differing only in case would overwrite each other on Windows.</summary>
    private static bool IsSafeKey(string key) => SafeName().IsMatch(key) && !ReservedName().IsMatch(key);

    [GeneratedRegex(@"(?:fill|stroke|stop-color)\s*[:=]\s*[""']?(#[0-9A-Fa-f]{3,8})\b")]
    private static partial Regex ColorUse();

    public static GenerateResult Generate(GenerateOptions options, Func<string, string?>? validate = null)
    {
        var definitions = ReadDefinitions(options.ManifestPath, options.IconsDir);
        var rules = options.ExcludePath is null ? [] : ReadExcludeRules(options.ExcludePath, definitions);
        var excluded = rules.ToDictionary(r => r.Key, StringComparer.Ordinal);
        foreach (var required in new[] { FileKey, FolderKey, FolderOpenKey })
        {
            if (!definitions.ContainsKey(required) || excluded.ContainsKey(required))
            {
                throw new InvalidOperationException($"the generic icon '{required}' must exist and must not be excluded");
            }
        }

        var outputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var failures = new List<IconFailure>();
        string Template(string key, string svg)
        {
            var template = key.StartsWith(FolderKey + "-", StringComparison.Ordinal) && key.Contains("-open", StringComparison.Ordinal) ? FolderOpenKey
                : key.StartsWith(FolderKey, StringComparison.Ordinal) ? FolderKey : FileKey;
            return SvgConverter.Convert(File.ReadAllText(definitions[template]), new SvgConvertOptions(MainColor(svg)));
        }

        foreach (var (key, path) in definitions)
        {
            var svg = File.ReadAllText(path);
            if (excluded.TryGetValue(key, out var rule))
            {
                outputs[key] = rule.ReplaceWith is { } other
                    ? SvgConverter.Convert(File.ReadAllText(definitions[other]))
                    : Template(key, svg);
                continue;
            }

            try
            {
                var xaml = SvgConverter.Convert(svg);
                if (validate?.Invoke(xaml) is { } error)
                {
                    throw new SvgConversionException(error);
                }

                outputs[key] = xaml;
            }
            catch (SvgConversionException ex)
            {
                failures.Add(new IconFailure(key, ex.Message));
                outputs[key] = Template(key, svg);
            }
        }

        var extra = new List<string>();
        if (options.ExtraDir is not null && Directory.Exists(options.ExtraDir))
        {
            foreach (var file in Directory.GetFiles(options.ExtraDir, "*.svg").Order(StringComparer.Ordinal))
            {
                var key = Path.GetFileNameWithoutExtension(file);
                if (!IsSafeKey(key) || definitions.Keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"DotDeck icon '{key}' has an invalid name or collides with a Material icon");
                }

                // DotDeck's own icons must convert; a failure here is a bug in the artwork.
                var xaml = SvgConverter.Convert(File.ReadAllText(file));
                if (validate?.Invoke(xaml) is { } error)
                {
                    throw new SvgConversionException($"{key}: {error}");
                }

                outputs[key] = xaml;
                extra.Add(key);
            }
        }

        Directory.CreateDirectory(options.OutputDir);
        var removed = 0;
        foreach (var stale in Directory.GetFiles(options.OutputDir, "*.xaml"))
        {
            if (!outputs.ContainsKey(Path.GetFileNameWithoutExtension(stale)))
            {
                File.Delete(stale);
                removed++;
            }
        }

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var (key, xaml) in outputs)
        {
            var target = Path.Combine(options.OutputDir, key + ".xaml");
            var bytes = utf8.GetBytes(xaml);
            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
            {
                File.WriteAllBytes(target, bytes);
            }
        }

        return new GenerateResult(outputs.Count - failures.Count - rules.Count - extra.Count, failures, rules, extra, removed);
    }

    /// <summary>Icon key → SVG file, checked to be a plain file name inside the icons folder.</summary>
    public static SortedDictionary<string, string> ReadDefinitions(string manifestPath, string iconsDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in doc.RootElement.GetProperty("iconDefinitions").EnumerateObject())
        {
            if (!seen.Add(def.Name))
            {
                throw new InvalidOperationException($"icon keys differing only in case: '{def.Name}'");
            }

            var iconPath = def.Value.GetProperty("iconPath").GetString() ?? string.Empty;
            var fileName = iconPath.Replace('\\', '/').Split('/').Last();
            if (!IsSafeKey(def.Name) || !SafeName().IsMatch(fileName) || !fileName.EndsWith(".svg", StringComparison.Ordinal)
                || iconPath.Replace('\\', '/') != "./../icons/" + fileName)
            {
                throw new InvalidOperationException($"unexpected icon definition '{def.Name}' → '{iconPath}'");
            }

            var file = Path.Combine(iconsDir, fileName);
            if (!File.Exists(file) || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException($"icon file for '{def.Name}' not found or is a link: {fileName}");
            }

            result[def.Name] = file;
        }

        return result;
    }

    public static IReadOnlyList<ExcludeRule> ReadExcludeRules(string path, IReadOnlyDictionary<string, string> definitions)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var rules = new List<ExcludeRule>();
        var errors = new List<string>();
        if (doc.RootElement.TryGetProperty("icons", out var icons))
        {
            foreach (var item in icons.EnumerateArray())
            {
                var key = item.TryGetProperty("key", out var k) ? k.GetString() : null;
                var reason = item.TryGetProperty("reason", out var r) ? r.GetString() : null;
                var replace = item.TryGetProperty("replaceWith", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
                if (string.IsNullOrWhiteSpace(key) || !definitions.ContainsKey(key))
                {
                    errors.Add($"excluded key '{key}' is not a Material icon");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(reason))
                {
                    errors.Add($"excluded key '{key}' needs a reason");
                    continue;
                }

                if (rules.Any(x => x.Key == key))
                {
                    errors.Add($"excluded key '{key}' is listed twice");
                    continue;
                }

                rules.Add(new ExcludeRule(key, reason, replace));
            }
        }

        foreach (var rule in rules.Where(x => x.ReplaceWith is not null))
        {
            if (!definitions.ContainsKey(rule.ReplaceWith!) || rules.Any(x => x.Key == rule.ReplaceWith))
            {
                errors.Add($"'{rule.Key}' replaceWith '{rule.ReplaceWith}' must be an existing, not excluded icon");
            }
        }

        // An outdated list (an icon renamed upstream) must be fixed, not silently ignored.
        return errors.Count > 0 ? throw new InvalidOperationException(string.Join(Environment.NewLine, errors)) : rules;
    }

    /// <summary>The most used non-neutral color of an SVG, so a stand-in keeps the icon's color family.</summary>
    public static string MainColor(string svg)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in ColorUse().Matches(svg))
        {
            try
            {
                var c = SvgValues.Color(m.Groups[1].Value);
                var max = Math.Max(c.R, Math.Max(c.G, c.B));
                var min = Math.Min(c.R, Math.Min(c.G, c.B));
                if (c.A == 0 || (max - min < 24 && (max < 40 || min > 215)))
                {
                    continue; // transparent, black, white and near-greys say nothing about the color family
                }

                var hex = SvgValues.ColorXaml((255, c.R, c.G, c.B))[3..];
                counts[hex] = counts.GetValueOrDefault(hex) + 1;
            }
            catch (SvgConversionException)
            {
                // ignore malformed colors here; conversion reports them
            }
        }

        return counts.Count == 0 ? FallbackColor
            : "#" + counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
    }

    /// <summary>Markdown summary for SOURCE.md.</summary>
    public static string Report(GenerateResult result)
    {
        var sb = new StringBuilder();
        sb.Append("| 항목 | 개수 |\n|---|---|\n");
        sb.Append($"| 변환 성공 | {result.Converted} |\n| 변환 실패(기본 아이콘으로 대체) | {result.Failed.Count} |\n");
        sb.Append($"| 브랜드 제외(대체) | {result.Excluded.Count} |\n| DotDeck 전용 | {result.Extra.Count} |\n\n");
        if (result.Failed.Count > 0)
        {
            sb.Append("### 변환 실패\n\n");
            foreach (var f in result.Failed)
            {
                sb.Append($"- `{f.Key}`: {f.Reason}\n");
            }

            sb.Append('\n');
        }

        if (result.Excluded.Count > 0)
        {
            sb.Append("### 브랜드 제외\n\n");
            foreach (var e in result.Excluded)
            {
                sb.Append($"- `{e.Key}` → {(e.ReplaceWith is null ? "일반 아이콘(같은 색 계열)" : $"`{e.ReplaceWith}`")}: {e.Reason}\n");
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
}
