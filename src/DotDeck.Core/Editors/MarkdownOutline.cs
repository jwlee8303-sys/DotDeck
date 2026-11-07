namespace DotDeck.Core.Editors;

public sealed record OutlineHeading(int Level, string Title, int Line);

/// <summary>ATX headings (<c># Title</c>) of a Markdown text for the table-of-contents navigation.</summary>
public static class MarkdownOutline
{
    public static IReadOnlyList<OutlineHeading> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<OutlineHeading>();
        var lines = text.Split('\n');
        var start = 0;

        // Skip YAML frontmatter.
        if (lines.Length > 0 && lines[0].TrimEnd('\r').TrimStart('﻿') == "---")
        {
            var end = Array.FindIndex(lines, 1, l => l.TrimEnd('\r').TrimEnd() is "---" or "...");
            start = end > 0 ? end + 1 : 0;
        }

        string? fence = null;
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (line.Length - trimmed.Length <= 3 && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                var marker = trimmed[..3];
                fence = fence is null ? marker : fence == marker ? null : fence;
                continue;
            }

            if (fence is not null || line.Length - trimmed.Length > 3 || !trimmed.StartsWith('#'))
            {
                continue;
            }

            var level = trimmed.TakeWhile(c => c == '#').Count();
            if (level > 6 || (trimmed.Length > level && trimmed[level] != ' ' && trimmed[level] != '\t'))
            {
                continue;
            }

            var title = trimmed[level..].Trim().TrimEnd('#').TrimEnd();
            if (title.Length > 0)
            {
                result.Add(new OutlineHeading(level, title, i + 1));
            }
        }

        return result;
    }
}
