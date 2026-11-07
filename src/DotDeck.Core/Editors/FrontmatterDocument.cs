using System.Text;

namespace DotDeck.Core.Editors;

/// <summary>
/// Markdown with a YAML frontmatter block (agents, skills). Supports the subset these files use — top-level
/// <c>key: value</c> entries with plain/quoted scalars, block scalars (<c>|</c>, <c>&gt;</c>), inline
/// (<c>[a, b]</c>) and dash lists. Entries are kept as their original lines, so keys the form does not know (and
/// their formatting) survive a round trip; only entries the form sets are rewritten.
/// </summary>
public sealed class FrontmatterDocument
{
    private readonly List<Entry> _entries = [];
    private readonly string _newLine;

    private FrontmatterDocument(string newLine) => _newLine = newLine;

    public bool HasFrontmatter { get; private set; }

    /// <summary>Text after the closing <c>---</c> line (or the whole text without frontmatter).</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>1-based line of the first body line in the source text.</summary>
    public int BodyStartLine { get; private set; } = 1;

    /// <summary>Problems found while parsing (unclosed block, lines that are not entries).</summary>
    public IReadOnlyList<ValidationIssue> ParseIssues => _issues;

    private readonly List<ValidationIssue> _issues = [];

    public IEnumerable<string> Keys => _entries.Where(e => e.Key is not null).Select(e => e.Key!);

    public static FrontmatterDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var doc = new FrontmatterDocument(newLine);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var start = lines.Count > 0 && lines[0].TrimStart('﻿').TrimEnd() == "---";
        if (!start)
        {
            doc.Body = text;
            return doc;
        }

        var end = lines.FindIndex(1, l => l.TrimEnd() is "---" or "...");
        if (end < 0)
        {
            doc._issues.Add(new ValidationIssue(IssueSeverity.Error, "frontmatter가 닫히지 않았습니다(--- 줄 없음).", 1));
            doc.Body = text;
            return doc;
        }

        doc.HasFrontmatter = true;
        Entry? current = null;
        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            var isContinuation = line.Length > 0 && (char.IsWhiteSpace(line[0]) || line.StartsWith('-')) && current is not null;
            if (isContinuation || (line.Trim().Length == 0 && current is not null) || line.TrimStart().StartsWith('#'))
            {
                (current ?? doc.AddLoose()).Lines.Add(line);
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                doc._issues.Add(new ValidationIssue(IssueSeverity.Warning, $"frontmatter 항목으로 읽을 수 없는 줄입니다: {line.Trim()}", i + 1));
                doc.AddLoose().Lines.Add(line);
                current = null;
                continue;
            }

            current = new Entry(line[..colon].Trim(), i + 1);
            current.Lines.Add(line);
            doc._entries.Add(current);
        }

        doc.BodyStartLine = end + 2;
        doc.Body = string.Join(newLine, lines.Skip(end + 1));
        return doc;
    }

    public bool Contains(string key) => Find(key) is not null;

    /// <summary>1-based source line of <paramref name="key"/>, if present.</summary>
    public int? LineOf(string key) => Find(key)?.Line;

    /// <summary>Scalar value (block scalars joined with new lines), or null when the key is missing.</summary>
    public string? Get(string key)
    {
        if (Find(key) is not { } entry)
        {
            return null;
        }

        var first = entry.Lines[0];
        var value = first[(first.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
        if (value is "|" or ">" or "|-" or ">-" or "|+" or ">+")
        {
            // Column-0 comments are top-level YAML comments, not block content.
            var block = entry.Lines.Skip(1).Where(l => !l.StartsWith('#')).ToList();
            while (block.Count > 0 && block[^1].Trim().Length == 0)
            {
                block.RemoveAt(block.Count - 1);
            }

            var indent = block.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
            var parts = block.Select(l => l.Length >= indent ? l[indent..] : l.Trim());
            return value.StartsWith('>') ? string.Join(" ", parts.Select(p => p.Trim())).Trim() : string.Join("\n", parts);
        }

        if (value.Length == 0 && entry.Lines.Count > 1)
        {
            // Dash list rendered as a comma separated value.
            return string.Join(", ", ListItems(entry.Lines.Skip(1)));
        }

        return Unquote(value);
    }

    /// <summary>List value: inline <c>[a, b]</c>, dash list or comma separated scalar.</summary>
    public IReadOnlyList<string> GetList(string key)
    {
        if (Find(key) is not { } entry)
        {
            return [];
        }

        var first = entry.Lines[0];
        var value = first[(first.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            value = value[1..^1];
        }
        else if (value.Length == 0)
        {
            return ListItems(entry.Lines.Skip(1)).ToList();
        }

        return value.Split(',').Select(v => Unquote(v.Trim())).Where(v => v.Length > 0).ToList();
    }

    /// <summary>Sets a scalar (multi-line values become a <c>|</c> block). Null removes the key.</summary>
    public void Set(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var entry = Find(key);
        if (value is null)
        {
            if (entry is not null)
            {
                var index = _entries.IndexOf(entry);
                var trailing = Trailing(entry);
                _entries.RemoveAt(index);
                if (trailing.Count > 0)
                {
                    var loose = new Entry(null, 0);
                    loose.Lines.AddRange(trailing);
                    _entries.Insert(index, loose);
                }
            }

            return;
        }

        var lines = new List<string>();
        if (value.Contains('\n', StringComparison.Ordinal))
        {
            lines.Add($"{key}: |");
            lines.AddRange(value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Length == 0 ? string.Empty : "  " + l));
        }
        else
        {
            lines.Add($"{key}: {Quote(value)}");
        }

        Replace(key, entry, lines);
    }

    /// <summary>Sets a list as a comma separated scalar (the form Claude Code documents for <c>tools</c>).</summary>
    public void SetList(string key, IReadOnlyList<string>? items)
    {
        if (items is null || items.Count == 0)
        {
            Set(key, null);
            return;
        }

        Replace(key, Find(key), [$"{key}: {Quote(string.Join(", ", items))}"]);
    }

    public override string ToString()
    {
        if (!HasFrontmatter && _entries.Count == 0)
        {
            return Body;
        }

        var sb = new StringBuilder();
        sb.Append("---").Append(_newLine);
        foreach (var line in _entries.SelectMany(e => e.Lines))
        {
            sb.Append(line).Append(_newLine);
        }

        sb.Append("---").Append(_newLine);
        sb.Append(Body);
        return sb.ToString();
    }

    private Entry? Find(string key) =>
        _entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.Ordinal));

    private Entry AddLoose()
    {
        var entry = new Entry(null, 0);
        _entries.Add(entry);
        return entry;
    }

    private void Replace(string key, Entry? entry, List<string> lines)
    {
        if (entry is null)
        {
            entry = new Entry(key, 0);
            _entries.Add(entry);
            HasFrontmatter = true;
        }

        // Keep blank/comment lines that trailed the old value (they separate entries visually).
        var trailing = Trailing(entry);
        entry.Lines.Clear();
        entry.Lines.AddRange(lines);
        entry.Lines.AddRange(trailing);
    }

    private static List<string> Trailing(Entry entry) =>
        entry.Lines.Skip(1).Reverse().TakeWhile(l => l.Trim().Length == 0 || l.StartsWith('#')).Reverse().ToList();

    private static IEnumerable<string> ListItems(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim()).Where(l => l.StartsWith('-')).Select(l => Unquote(l[1..].Trim())).Where(v => v.Length > 0);

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            var inner = value[1..^1];
            return value[0] == '"'
                ? inner.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal)
                : inner.Replace("''", "'", StringComparison.Ordinal);
        }

        // Plain scalar: drop a trailing comment.
        var hash = value.IndexOf(" #", StringComparison.Ordinal);
        return hash >= 0 ? value[..hash].TrimEnd() : value;
    }

    // Quote only when YAML would read the plain value differently.
    private static string Quote(string value)
    {
        var needs = value.Length == 0 || value != value.Trim()
            || value.Contains(": ", StringComparison.Ordinal) || value.Contains(" #", StringComparison.Ordinal) || value.EndsWith(':')
            || "[]{}&*!|>'\"%@`-?,#".Contains(value[0], StringComparison.Ordinal)
            || value is "true" or "false" or "null" or "yes" or "no" or "~";
        return needs ? "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;
    }

    private sealed class Entry(string? key, int line)
    {
        public string? Key { get; } = key;

        public int Line { get; } = line;

        public List<string> Lines { get; } = [];
    }
}
