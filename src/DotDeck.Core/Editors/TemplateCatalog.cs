using DotDeck.Core.Explorer;
using DotDeck.Core.Files;

namespace DotDeck.Core.Editors;

/// <summary>A starting text for a new document. <paramref name="FileName"/> is the suggested file name.</summary>
public sealed record DocumentTemplate(string Name, string FileName, string Text, bool IsBuiltIn);

/// <summary>
/// Built-in templates (agent, skill, settings, CLAUDE.md, AGENTS.md) plus the user's files in
/// <c>%APPDATA%\DotDeck\templates\</c> (the file name is the suggested name; secret, large and binary files are skipped).
/// </summary>
public static class TemplateCatalog
{
    public const long MaxTemplateBytes = 256 * 1024;

    public static IReadOnlyList<DocumentTemplate> BuiltIn { get; } =
    [
        new("서브에이전트 (agents/*.md)", "my-agent.md",
            "---\nname: my-agent\ndescription: 언제 이 에이전트를 쓰는지 한두 문장으로 설명합니다.\ntools: Read, Grep, Glob\nmodel: inherit\n---\n\n당신은 ... 전문가입니다.\n\n## 작업 방식\n- \n", true),
        new("스킬 (skills/<이름>/SKILL.md)", "SKILL.md",
            "---\nname: my-skill\ndescription: 이 스킬이 하는 일과 언제 쓰는지 설명합니다.\n---\n\n# My Skill\n\n## 절차\n1. \n", true),
        new("Claude 설정 (settings.json)", "settings.json",
            "{\n  \"permissions\": {\n    \"allow\": [],\n    \"deny\": []\n  },\n  \"hooks\": {},\n  \"env\": {}\n}\n", true),
        new("CLAUDE.md", "CLAUDE.md",
            "# 프로젝트 지침\n\n## 스택\n- \n\n## 명령어\n- 빌드: \n- 테스트: \n\n## 규칙\n- \n", true),
        new("AGENTS.md", "AGENTS.md",
            "# AGENTS.md\n\n## 개요\n\n## 빌드·테스트\n\n## 코드 규칙\n", true),
    ];

    /// <summary>Built-in templates followed by the user's templates (sorted by name).</summary>
    public static IReadOnlyList<DocumentTemplate> Load(IFileStore store, string userDirectory)
    {
        ArgumentNullException.ThrowIfNull(store);
        var result = new List<DocumentTemplate>(BuiltIn);
        if (!store.DirectoryExists(userDirectory))
        {
            return result;
        }

        foreach (var entry in store.GetChildren(userDirectory)
            .Where(e => !e.IsDirectory && !e.IsReparsePoint && e.Length <= MaxTemplateBytes && !SecretFilePolicy.IsSecret(e.Name))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                var bytes = store.ReadAllBytes(entry.FullPath);
                if (Array.IndexOf(bytes, (byte)0) >= 0)
                {
                    continue;
                }

                var text = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
                result.Add(new DocumentTemplate(entry.Name, entry.Name, text, false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            {
                // Unreadable or not UTF-8: not offered.
            }
        }

        return result;
    }
}
