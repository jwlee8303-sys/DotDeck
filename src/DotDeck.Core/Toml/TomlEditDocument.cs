using System.Text;
using System.Text.Json;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace DotDeck.Core.Toml;

public sealed class TomlEditException(string message, int line = 1, int column = 1) : Exception(message)
{
    public int Line { get; } = line;
    public int Column { get; } = column;
}

/// <summary>UTF-8 TOML source with surgical value patches. Tomlyn parses; it never serializes the document.</summary>
public sealed class TomlEditDocument
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);
    private readonly bool _bom;
    private string _text;
    private DocumentSyntax _syntax;
    private readonly Dictionary<string, ValueSyntax> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _sections = new(StringComparer.Ordinal);

    private TomlEditDocument(string text, bool bom)
    {
        _text = text;
        _bom = bom;
        _syntax = ParseSyntax(text);
        Index();
    }

    public static TomlEditDocument Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble());
        try
        {
            return new TomlEditDocument(s_utf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
        }
        catch (DecoderFallbackException)
        {
            throw new TomlEditException("UTF-8 TOML이 아닙니다. 인코딩을 확인하세요.");
        }
    }

    public byte[] ToBytes() => [.. _bom ? Encoding.UTF8.GetPreamble() : [], .. s_utf8.GetBytes(_text)];

    public void SetString(IReadOnlyList<string> path, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var old = _values.GetValueOrDefault(Key(path));
        if (old is StringValueSyntax existing && existing.Value == value)
        {
            return;
        }
        var original = old is null ? null : Slice(old);
        string literal;
        if (original?.StartsWith("'''", StringComparison.Ordinal) == true && !value.Contains("'''", StringComparison.Ordinal)
            && !value.StartsWith('\n') && !value.StartsWith('\r') && !value.EndsWith('\'')
            && value.All(c => c >= ' ' && c != '\u007f' || c is '\n' or '\r' or '\t'))
        {
            literal = "'''" + value + "'''";
        }
        else if (!value.Contains('\n') && !value.Contains('\r') && !value.Contains('\'')
            && (original?.StartsWith('\'') == true || (original is null && value.Contains('\\')))
            && value.All(c => c >= ' ' && c != '\u007f'))
        {
            literal = "'" + value + "'";
        }
        else
        {
            literal = JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (value.Contains('\n') || original?.StartsWith("\"\"\"", StringComparison.Ordinal) == true)
            {
                // Keep escapes (including the first newline) to avoid TOML's leading-newline trimming.
                literal = "\"\"\"" + literal[1..^1] + "\"\"\"";
            }
        }

        var check = ParseSyntax("value = " + literal).KeyValues.First().Value as StringValueSyntax;
        if (check?.Value != value)
        {
            throw new TomlEditException("문자열을 손실 없이 표현할 수 없어 변경을 취소했습니다.");
        }

        SetValue(path, literal);
    }

    /// <summary>Sets one TOML literal. Paths contain decoded key segments; array indices are decimal segments.</summary>
    public void SetValue(IReadOnlyList<string> path, string literal)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0 || path.Any(p => p is null))
        {
            throw new ArgumentException("A nonempty key path is required.", nameof(path));
        }

        var isolated = ParseSyntax("value = " + literal);
        if (isolated.KeyValues.Count() != 1 || isolated.Tables.Count() != 0
            || isolated.KeyValues.First().Value is not { } parsed
            || ("value = " + literal).Substring(parsed.Span.Start.Offset, parsed.Span.Length) != literal.Trim())
        {
            throw new TomlEditException("하나의 TOML 값만 입력할 수 있습니다.");
        }

        literal = literal.Trim();
        var key = Key(path);
        string candidate;
        if (_values.TryGetValue(key, out var node))
        {
            candidate = _text[..node.Span.Start.Offset] + literal + _text[(node.Span.Start.Offset + node.Span.Length)..];
        }
        else
        {
            var parent = Key(path.Take(path.Count - 1));
            if (!_sections.TryGetValue(parent, out var offset))
            {
                throw new TomlEditException("기존 명시적 테이블 또는 루트에만 키를 추가할 수 있습니다.");
            }

            var newline = _text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var prefix = offset > 0 && _text[offset - 1] != '\n' ? newline : string.Empty;
            candidate = _text[..offset] + prefix + QuoteKey(path[^1]) + " = " + literal + newline + _text[offset..];
        }

        // Build and verify a separate document before publishing any change.
        var next = new TomlEditDocument(candidate, _bom);
        if (!next._values.TryGetValue(key, out var changed) || next.Slice(changed) != literal)
        {
            throw new TomlEditException("수정한 값의 경로를 확인할 수 없어 변경을 취소했습니다.");
        }

        _text = next._text;
        _syntax = next._syntax;
        Index();
    }

    private static DocumentSyntax ParseSyntax(string text)
    {
        var syntax = SyntaxParser.Parse(text, validate: true);
        if (syntax.HasErrors)
        {
            var error = syntax.Diagnostics.First();
            throw new TomlEditException(error.Message, error.Span.Start.Line + 1, error.Span.Start.Column + 1);
        }

        return syntax;
    }

    private void Index()
    {
        var tables = _syntax.Tables.ToArray();
        _values.Clear();
        _sections.Clear();
        _sections[Key([])] = tables.Length == 0 ? _text.Length : tables[0].Span.Start.Offset;
        foreach (var pair in _syntax.KeyValues)
        {
            IndexPair([], pair);
        }

        var activeArrays = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < tables.Length; i++)
        {
            var table = tables[i];
            var name = Segments(table.Name!);
            var expanded = new List<string>();
            for (var j = 0; j < name.Count; j++)
            {
                expanded.Add(name[j]);
                var arrayKey = Key(expanded);
                if (j == name.Count - 1 && table is TableArraySyntax)
                {
                    activeArrays[arrayKey] = activeArrays.GetValueOrDefault(arrayKey, -1) + 1;
                }

                if (activeArrays.TryGetValue(arrayKey, out var index))
                {
                    expanded.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            _sections[Key(expanded)] = i + 1 < tables.Length ? tables[i + 1].Span.Start.Offset : _text.Length;
            foreach (var pair in table.Items)
            {
                IndexPair(expanded, pair);
            }
        }
    }

    private void IndexPair(IReadOnlyList<string> prefix, KeyValueSyntax pair)
    {
        List<string> path = [.. prefix, .. Segments(pair.Key!)];
        if (pair.Value is { } value)
        {
            IndexValue(path, value);
        }
    }

    private void IndexValue(IReadOnlyList<string> path, ValueSyntax value)
    {
        _values.Add(Key(path), value);
        if (value is InlineTableSyntax inline)
        {
            foreach (var item in inline.Items)
            {
                IndexPair(path, item.KeyValue!);
            }
        }
        else if (value is ArraySyntax array)
        {
            for (var i = 0; i < array.Items.Count(); i++)
            {
                IndexValue([.. path, i.ToString(System.Globalization.CultureInfo.InvariantCulture)], array.Items.ElementAt(i).Value!);
            }
        }
    }

    private string Slice(ValueSyntax value) => _text.Substring(value.Span.Start.Offset, value.Span.Length);
    private static string Key(IEnumerable<string> path) => JsonSerializer.Serialize(path);
    private static string QuoteKey(string key) => BareKeySyntax.IsBareKey(key) ? key : JsonSerializer.Serialize(key);
    private static List<string> Segments(KeySyntax key) => [DecodeKey(key.Key!), .. key.DotKeys.Select(k => DecodeKey(k.Key!))];
    private static string DecodeKey(BareKeyOrStringValueSyntax key) => key switch
    {
        BareKeySyntax bare => bare.Key!.Text!,
        StringValueSyntax text => text.Value!,
        _ => throw new TomlEditException("지원하지 않는 TOML 키입니다."),
    };
}
