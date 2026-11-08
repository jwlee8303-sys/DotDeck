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
    public sealed record Entry(IReadOnlyList<string> Path, string Literal, bool CanDelete);
    public IReadOnlyList<Entry> Entries => [.. _values.Select(pair => new Entry(
        JsonSerializer.Deserialize<string[]>(pair.Key)!, Slice(pair.Value), _assignments.ContainsKey(pair.Key)))];
    public static string FormatPath(IEnumerable<string> path) => string.Join(".", path.Select(QuoteKey));
    public static IReadOnlyList<string> ParsePath(string path)
    {
        var syntax = ParseSyntax(path + " = 0");
        if (syntax.KeyValues.Count() != 1 || syntax.Tables.Any()) throw new TomlEditException("키 경로를 확인하세요.");
        return Segments(syntax.KeyValues.First().Key!);
    }
    public bool CanSetValue(IReadOnlyList<string> path) => path.Count > 0
        && (_values.ContainsKey(Key(path)) || _sections.ContainsKey(Key(path.Take(path.Count - 1))));
    public IReadOnlyList<IReadOnlyList<string>> TablePaths => [.. _tablePaths.Values];

    public void SetStringList(IReadOnlyList<string> path, IReadOnlyList<string> values)
    {
        var work = new TomlEditDocument(_text, _bom);
        if (!work._values.TryGetValue(Key(path), out var found) || found is not ArraySyntax)
        {
            work.SetValue(path, "[" + string.Join(", ", values.Select(v => JsonSerializer.Serialize(v))) + "]");
        }
        else
        {
            while (((ArraySyntax)work._values[Key(path)]).Items.Count() != values.Count)
            {
                var array = (ArraySyntax)work._values[Key(path)];
                var items = array.Items.ToArray();
                var patches = new List<(int Start, int Length, string Text)>();
                if (items.Length > values.Count)
                {
                    var last = items[^1];
                    patches.Add((last.Value!.Span.Start.Offset, last.Value.Span.Length, ""));
                    var comma = last.Comma ?? (items.Length > 1 ? items[^2].Comma : null);
                    if (comma is not null) patches.Add((comma.Span.Start.Offset, comma.Span.Length, ""));
                }
                else
                {
                    if (items.Length > 0 && items[^1].Comma is null)
                    {
                        var last = items[^1].Value!;
                        patches.Add((last.Span.Start.Offset + last.Span.Length, 0, ","));
                    }
                    patches.Add((array.CloseBracket!.Span.Start.Offset, 0, JsonSerializer.Serialize(values[items.Length])));
                }
                var text = work._text;
                foreach (var p in patches.OrderByDescending(p => p.Start)) text = text[..p.Start] + p.Text + text[(p.Start + p.Length)..];
                work = new TomlEditDocument(text, _bom);
            }
            for (var i = 0; i < values.Count; i++) work.SetString([.. path, i.ToString(System.Globalization.CultureInfo.InvariantCulture)], values[i]);
        }
        _text = work._text; _syntax = work._syntax; Index();
    }

    public void DeleteTable(IReadOnlyList<string> path)
    {
        if (path.Count == 0) throw new TomlEditException("루트 테이블은 삭제할 수 없습니다.");
        var ranges = _assignments.Where(p => IsPrefix(path, JsonSerializer.Deserialize<string[]>(p.Key)!))
            .Select(p => (Start: LineStart(p.Value.Key!.Span.Start.Offset), End: LineEnd(p.Value.Value!.Span.Start.Offset + p.Value.Value.Span.Length)))
            .Concat(_tablePaths.Where(p => IsPrefix(path, p.Value)).Select(p =>
                (Start: LineStart(p.Key.OpenBracket!.Span.Start.Offset), End: LineEnd(p.Key.CloseBracket!.Span.Start.Offset + p.Key.CloseBracket.Span.Length))));
        var candidate = _text;
        foreach (var range in ranges.Distinct().OrderByDescending(r => r.Start)) candidate = candidate[..range.Start] + candidate[range.End..];
        Adopt(candidate, path);
    }

    public void RenameTable(IReadOnlyList<string> path, string newName)
    {
        if (path.Count == 0 || string.IsNullOrEmpty(newName)) throw new TomlEditException("테이블 이름을 입력하세요.");
        IReadOnlyList<string> renamed = [.. path.Take(path.Count - 1), newName];
        var patches = new List<(int Start, int Length, string Text)>();
        foreach (var (table, tablePath) in _tablePaths.Where(p => IsPrefix(path, p.Value)))
        {
            patches.Add((table.Name!.Span.Start.Offset, table.Name.Span.Length, FormatPath([.. renamed, .. tablePath.Skip(path.Count)])));
        }
        foreach (var (key, pair) in _assignments)
        {
            var full = JsonSerializer.Deserialize<string[]>(key)!;
            var local = Segments(pair.Key!);
            var parentLength = full.Length - local.Count;
            if (IsPrefix(path, full) && parentLength < path.Count)
            {
                patches.Add((pair.Key!.Span.Start.Offset, pair.Key.Span.Length,
                    FormatPath(renamed.Concat(full.Skip(path.Count)).Skip(parentLength))));
            }
        }
        if (patches.Count == 0) throw new TomlEditException("테이블을 찾을 수 없습니다.");
        var candidate = _text;
        foreach (var patch in patches.OrderByDescending(p => p.Start)) candidate = candidate[..patch.Start] + patch.Text + candidate[(patch.Start + patch.Length)..];
        var next = new TomlEditDocument(candidate, _bom);
        foreach (var (key, value) in _values)
        {
            var originalPath = JsonSerializer.Deserialize<string[]>(key)!;
            var expected = IsPrefix(path, originalPath) ? Key(renamed.Concat(originalPath.Skip(path.Count))) : key;
            if (!next._values.TryGetValue(expected, out var other) || Slice(value) != next.Slice(other)) throw new TomlEditException("이름 변경 검증 실패");
        }
        _text = next._text; _syntax = next._syntax; Index();
    }
    private static readonly UTF8Encoding s_utf8 = new(false, true);
    private readonly bool _bom;
    private string _text;
    private DocumentSyntax _syntax;
    private readonly Dictionary<string, ValueSyntax> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _sections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KeyValueSyntax> _assignments = new(StringComparer.Ordinal);
    private readonly Dictionary<TableSyntaxBase, IReadOnlyList<string>> _tablePaths = [];

    public void CreateTable(IReadOnlyList<string> path)
    {
        if (path.Count == 0) throw new ArgumentException("A table path is required.", nameof(path));
        var newline = _text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var separator = _text.Length > 0 && !_text.EndsWith('\n') ? newline : string.Empty;
        Adopt(_text + separator + "[" + string.Join(".", path.Select(QuoteKey)) + "]" + newline);
    }

    public bool CanDeleteKey(IReadOnlyList<string> path) => _assignments.ContainsKey(Key(path));

    /// <summary>Removes the assignment's physical lines, inline comment and final newline. Standalone comments stay.</summary>
    public void DeleteKey(IReadOnlyList<string> path)
    {
        if (!_assignments.TryGetValue(Key(path), out var pair))
            throw new TomlEditException("이 키의 삭제는 지원하지 않습니다(인라인 항목·배열 원소 제외).");
        var start = LineStart(pair.Key!.Span.Start.Offset);
        var end = LineEnd(pair.Value!.Span.Start.Offset + pair.Value.Span.Length);
        Adopt(_text[..start] + _text[end..], path);
    }

    /// <summary>Removes empty ordinary table headers only. Array tables and parents of remaining tables stay.</summary>
    public void RemoveEmptyTables()
    {
        while (true)
        {
            var empty = _tablePaths.Reverse().FirstOrDefault(entry => entry.Key is not TableArraySyntax
                && !entry.Key.Items.Any()
                && !_tablePaths.Any(other => other.Key != entry.Key && IsPrefix(entry.Value, other.Value)));
            if (empty.Key is null) return;
            var start = LineStart(empty.Key.OpenBracket!.Span.Start.Offset);
            var end = LineEnd(empty.Key.CloseBracket!.Span.Start.Offset + empty.Key.CloseBracket.Span.Length);
            Adopt(_text[..start] + _text[end..]);
        }
    }

    private static bool IsPrefix(IReadOnlyList<string> prefix, IReadOnlyList<string> path) =>
        prefix.Count <= path.Count && prefix.SequenceEqual(path.Take(prefix.Count));

    private int LineStart(int offset) => _text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
    private int LineEnd(int offset)
    {
        var newline = _text.IndexOf('\n', offset);
        return newline < 0 ? _text.Length : newline + 1;
    }

    private void Adopt(string candidate, IReadOnlyList<string>? removed = null)
    {
        var next = new TomlEditDocument(candidate, _bom);
        foreach (var (key, value) in _values)
        {
            var path = JsonSerializer.Deserialize<string[]>(key)!;
            if (removed is not null && IsPrefix(removed, path)) continue;
            if (!next._values.TryGetValue(key, out var other) || Slice(value) != next.Slice(other))
                throw new TomlEditException("다른 값이 바뀌어 변경을 취소했습니다.");
        }
        _text = next._text;
        _syntax = next._syntax;
        Index();
    }

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
        _assignments.Clear();
        _tablePaths.Clear();
        _sections[Key([])] = tables.Length == 0 ? _text.Length : tables[0].Span.Start.Offset;
        foreach (var pair in _syntax.KeyValues)
        {
            IndexPair([], pair, true);
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
            _tablePaths[table] = expanded;
            foreach (var pair in table.Items)
            {
                IndexPair(expanded, pair, true);
            }
        }
    }

    private void IndexPair(IReadOnlyList<string> prefix, KeyValueSyntax pair, bool standalone = false)
    {
        List<string> path = [.. prefix, .. Segments(pair.Key!)];
        if (standalone) _assignments[Key(path)] = pair;
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
