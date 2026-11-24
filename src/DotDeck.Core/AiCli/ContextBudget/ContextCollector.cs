using System.Text;
using System.Text.RegularExpressions;
using DotDeck.Core.AiCli.Launch;

namespace DotDeck.Core.AiCli.ContextBudget;

/// <summary>
/// Finds what an AI CLI reads before the first prompt in a working folder: instruction files, skill and agent
/// descriptions, and MCP servers (configuration only; servers are never started here). Read-only, and files are read
/// as text only up to <see cref="MaxFileBytes"/>. Locations (2026-11-24):
/// <list type="bullet">
/// <item>Claude Code: <c>~/.claude/CLAUDE.md</c>, <c>~/.claude/rules</c>, <c>CLAUDE.md</c> / <c>.claude/CLAUDE.md</c> /
/// <c>CLAUDE.local.md</c> in every ancestor of the folder, <c>@import</c>s (depth 5), unconditional <c>.claude/rules</c>,
/// skills and agents of the user, the project and enabled plugins.</item>
/// <item>Codex: <c>AGENTS.override.md</c> or <c>AGENTS.md</c> in CODEX_HOME and from the git root down to the folder,
/// skills in <c>~/.codex/skills</c>, <c>~/.agents/skills</c> and <c>.agents/skills</c>.</item>
/// <item>agy (from its built-in "agy-customizations" guide): <c>GEMINI.md</c>, <c>AGENTS.md</c>, <c>.agents/rules</c> and
/// <c>.agents/skills</c> from the git root down to the folder, <c>~/.gemini/config</c> (rules, skills, plugins) and the
/// built-in skills. Only <c>always_on</c> rules (no trigger) count in full.</item>
/// </list>
/// </summary>
public sealed partial class ContextCollector(string home, string? codexHome = null)
{
    public const long MaxFileBytes = 1024 * 1024;
    public const int MaxImportDepth = 5;

    /// <summary>Codex reads at most this many bytes of project AGENTS.md files (project_doc_max_bytes default).</summary>
    public const int CodexProjectDocLimit = 32 * 1024;

    private string ClaudeHome => Path.Combine(home, ".claude");

    private string CodexHome => codexHome ?? Path.Combine(home, ".codex");

    private string GeminiHome => Path.Combine(home, ".gemini");

    public ContextCollection Collect(CliKind cli, string? workingDirectory)
    {
        var run = new Run();
        var folder = NormalizeFolder(workingDirectory);
        switch (cli)
        {
            case CliKind.Claude:
                CollectClaude(run, folder);
                break;
            case CliKind.Codex:
                CollectCodex(run, folder);
                break;
            default:
                CollectAgy(run, folder);
                break;
        }

        return new ContextCollection(run.Items, run.Servers, run.Problems, [.. run.Watch.Select(w => new ContextWatchTarget(w.Key, w.Value))]);
    }

    // ---------------------------------------------------------------- Claude

    private void CollectClaude(Run run, string? folder)
    {
        AddInstructionWithImports(run, Path.Combine(ClaudeHome, "CLAUDE.md"), "전역", 0);
        AddRules(run, Path.Combine(ClaudeHome, "rules"), "전역 규칙", claudeRules: true);
        run.WatchDirectory(ClaudeHome);
        foreach (var sub in new[] { "rules", "skills", "agents" })
        {
            run.WatchDirectory(Path.Combine(ClaudeHome, sub), recursive: true);
        }

        var chain = folder is null ? [] : AncestorsTopDown(folder, stopAtGitRoot: false);
        foreach (var dir in chain)
        {
            AddInstructionWithImports(run, Path.Combine(dir, "CLAUDE.md"), "프로젝트", 0);
            AddInstructionWithImports(run, Path.Combine(dir, ".claude", "CLAUDE.md"), "프로젝트", 0);
            AddInstructionWithImports(run, Path.Combine(dir, "CLAUDE.local.md"), "로컬", 0);
            AddRules(run, Path.Combine(dir, ".claude", "rules"), "프로젝트 규칙", claudeRules: true);
            run.WatchDirectory(dir);
            run.WatchDirectory(Path.Combine(dir, ".claude"), recursive: true);
        }

        AddSkillFolder(run, Path.Combine(ClaudeHome, "skills"), "전역");
        AddAgentFolder(run, Path.Combine(ClaudeHome, "agents"), "전역");
        foreach (var dir in chain)
        {
            AddSkillFolder(run, Path.Combine(dir, ".claude", "skills"), "프로젝트");
            AddAgentFolder(run, Path.Combine(dir, ".claude", "agents"), "프로젝트");
        }

        foreach (var plugin in EnabledClaudePlugins(run))
        {
            AddSkillFolder(run, Path.Combine(plugin.Root, "skills"), "플러그인 " + plugin.Name);
            AddAgentFolder(run, Path.Combine(plugin.Root, "agents"), "플러그인 " + plugin.Name);
            var pluginMcp = Path.Combine(plugin.Root, ".mcp.json");
            if (File.Exists(pluginMcp))
            {
                McpConfigReader.AddJsonServers(run.Servers, run.Problems, pluginMcp, "플러그인 " + plugin.Name, null,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["CLAUDE_PLUGIN_ROOT"] = plugin.Root });
            }
        }

        McpConfigReader.AddClaude(run.Servers, run.Problems, home, chain);
    }

    private sealed record ClaudePlugin(string Name, string Root);

    /// <summary><c>enabledPlugins</c> of <c>~/.claude/settings.json</c> → newest cached version folder of each.</summary>
    private List<ClaudePlugin> EnabledClaudePlugins(Run run)
    {
        var result = new List<ClaudePlugin>();
        var settings = Path.Combine(ClaudeHome, "settings.json");
        var enabled = McpConfigReader.EnabledPlugins(settings, run.Problems);
        var cache = Path.Combine(ClaudeHome, "plugins", "cache");
        foreach (var id in enabled)
        {
            var at = id.LastIndexOf('@');
            if (at <= 0 || at == id.Length - 1)
            {
                continue;
            }

            var pluginDir = Path.Combine(cache, id[(at + 1)..], id[..at]);
            if (!SafeName(id[(at + 1)..]) || !SafeName(id[..at]) || !Directory.Exists(pluginDir))
            {
                continue;
            }

            var newest = new DirectoryInfo(pluginDir).EnumerateDirectories().OrderByDescending(d => d.LastWriteTimeUtc).FirstOrDefault();
            if (newest is not null)
            {
                result.Add(new ClaudePlugin(id[..at], newest.FullName));
            }
        }

        return result;
    }

    // ---------------------------------------------------------------- Codex

    private void CollectCodex(Run run, string? folder)
    {
        AddFirstExisting(run, "전역", Path.Combine(CodexHome, "AGENTS.override.md"), Path.Combine(CodexHome, "AGENTS.md"));
        run.WatchDirectory(CodexHome);
        run.WatchDirectory(Path.Combine(CodexHome, "skills"), recursive: true);

        var chain = folder is null ? [] : AncestorsTopDown(folder, stopAtGitRoot: true);
        var projectBytes = 0L;
        foreach (var dir in chain)
        {
            if (AddFirstExisting(run, "프로젝트", Path.Combine(dir, "AGENTS.override.md"), Path.Combine(dir, "AGENTS.md")) is { } path)
            {
                projectBytes += new FileInfo(path).Length;
            }

            run.WatchDirectory(dir);
            run.WatchDirectory(Path.Combine(dir, ".agents"), recursive: true);
            run.WatchDirectory(Path.Combine(dir, ".codex"), recursive: true);
        }

        if (projectBytes > CodexProjectDocLimit)
        {
            run.Problems.Add($"프로젝트 AGENTS.md 합계가 {CodexProjectDocLimit / 1024}KiB를 넘어 Codex가 뒷부분을 읽지 않습니다 (project_doc_max_bytes).");
        }

        AddSkillFolder(run, Path.Combine(CodexHome, "skills"), "전역", recursive: true);
        AddSkillFolder(run, Path.Combine(home, ".agents", "skills"), "사용자");
        foreach (var dir in chain)
        {
            AddSkillFolder(run, Path.Combine(dir, ".agents", "skills"), "프로젝트");
        }

        McpConfigReader.AddCodexToml(run.Servers, run.Problems, Path.Combine(CodexHome, "config.toml"), "Codex config.toml");
        foreach (var dir in chain)
        {
            McpConfigReader.AddCodexToml(run.Servers, run.Problems, Path.Combine(dir, ".codex", "config.toml"), "프로젝트 .codex/config.toml");
        }
    }

    // ---------------------------------------------------------------- agy

    private void CollectAgy(Run run, string? folder)
    {
        var config = Path.Combine(GeminiHome, "config");
        AddInstruction(run, Path.Combine(GeminiHome, "GEMINI.md"), "전역");
        AddInstruction(run, Path.Combine(config, "GEMINI.md"), "전역");
        AddInstruction(run, Path.Combine(config, "AGENTS.md"), "전역");
        AddRules(run, Path.Combine(config, "rules"), "전역 규칙", claudeRules: false);
        AddSkillFolder(run, Path.Combine(GeminiHome, "skills"), "전역");
        AddSkillFolder(run, Path.Combine(config, "skills"), "전역");
        AddSkillFolder(run, Path.Combine(GeminiHome, "antigravity-cli", "builtin", "skills"), "기본 제공", builtIn: true);
        run.WatchDirectory(config, recursive: true);
        run.WatchDirectory(GeminiHome);
        run.WatchDirectory(Path.Combine(GeminiHome, "skills"), recursive: true);

        var plugins = Path.Combine(config, "plugins");
        foreach (var plugin in SafeDirectories(plugins))
        {
            var name = Path.GetFileName(plugin);
            AddRules(run, Path.Combine(plugin, "rules"), "플러그인 " + name, claudeRules: false);
            AddSkillFolder(run, Path.Combine(plugin, "skills"), "플러그인 " + name);
            AddAgentFolder(run, Path.Combine(plugin, "agents"), "플러그인 " + name);
        }

        var chain = folder is null ? [] : AncestorsTopDown(folder, stopAtGitRoot: true);
        foreach (var dir in chain)
        {
            AddInstruction(run, Path.Combine(dir, "GEMINI.md"), "프로젝트");
            AddInstruction(run, Path.Combine(dir, "AGENTS.md"), "프로젝트");
            foreach (var agentsDir in new[] { ".agents", ".agent", "_agents", "_agent" })
            {
                AddRules(run, Path.Combine(dir, agentsDir, "rules"), "프로젝트 규칙", claudeRules: false);
                AddSkillFolder(run, Path.Combine(dir, agentsDir, "skills"), "프로젝트");
                run.WatchDirectory(Path.Combine(dir, agentsDir), recursive: true);
            }

            run.WatchDirectory(dir);
        }

        McpConfigReader.AddJsonServers(run.Servers, run.Problems, Path.Combine(config, "mcp_config.json"), "agy mcp_config.json", null, null);
    }

    // ---------------------------------------------------------------- instructions

    private string? AddFirstExisting(Run run, string label, params string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                AddInstruction(run, path, label);
                return path;
            }
        }

        return null;
    }

    private void AddInstruction(Run run, string path, string label)
    {
        if (run.Seen(path) && ReadText(run, path) is { } text)
        {
            run.Items.Add(new ContextItem(ContextCategory.Instructions, DisplayName(path), path, TokenEstimator.Estimate(text), Detail: label));
        }
    }

    /// <summary>A CLAUDE.md and the files its <c>@path</c> lines import (outside code), each as its own row.</summary>
    private void AddInstructionWithImports(Run run, string path, string label, int depth)
    {
        if (!run.Seen(path) || ReadText(run, path) is not { } text)
        {
            return;
        }

        run.Items.Add(new ContextItem(ContextCategory.Instructions, DisplayName(path), path, TokenEstimator.Estimate(text),
            Detail: depth == 0 ? label : label + " · import"));
        if (depth >= MaxImportDepth)
        {
            return;
        }

        foreach (var import in Imports(text))
        {
            var target = ResolveImport(import, Path.GetDirectoryName(path)!);
            if (target is not null && File.Exists(target))
            {
                AddInstructionWithImports(run, target, label, depth + 1);
            }
        }
    }

    /// <summary><c>@path</c> references outside fenced blocks and inline code.</summary>
    internal static IEnumerable<string> Imports(string text)
    {
        var inFence = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            var withoutCode = InlineCode().Replace(line, " ");
            foreach (Match match in ImportReference().Matches(withoutCode))
            {
                yield return match.Groups[1].Value;
            }
        }
    }

    private string? ResolveImport(string reference, string baseDirectory)
    {
        try
        {
            var path = reference.StartsWith("~/", StringComparison.Ordinal) || reference.StartsWith("~\\", StringComparison.Ordinal)
                ? Path.Combine(home, reference[2..])
                : Path.IsPathFullyQualified(reference) ? reference : Path.Combine(baseDirectory, reference);
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rule files (<c>*.md</c>, recursive). Claude: a rule with a <c>paths</c> frontmatter loads only for matching files and
    /// is not counted. agy: <c>trigger: model_decision</c> counts its description only; other non-always-on triggers are
    /// not counted.
    /// </summary>
    private void AddRules(Run run, string directory, string label, bool claudeRules)
    {
        foreach (var file in SafeFiles(directory, "*.md", recursive: true))
        {
            if (!run.Seen(file) || ReadText(run, file) is not { } text)
            {
                continue;
            }

            var front = Frontmatter.Parse(text);
            if (claudeRules && front.Has("paths"))
            {
                continue;
            }

            if (!claudeRules && front.Get("trigger") is { } trigger && !trigger.Equals("always_on", StringComparison.OrdinalIgnoreCase))
            {
                if (trigger.Equals("model_decision", StringComparison.OrdinalIgnoreCase) && front.Get("description") is { } description)
                {
                    run.Items.Add(new ContextItem(ContextCategory.Instructions, DisplayName(file), file,
                        TokenEstimator.Estimate(description), Detail: label + " · 설명만"));
                }

                continue;
            }

            run.Items.Add(new ContextItem(ContextCategory.Instructions, DisplayName(file), file, TokenEstimator.Estimate(text), Detail: label));
        }
    }

    // ---------------------------------------------------------------- skills and agents

    /// <summary><c>&lt;dir&gt;/&lt;skill&gt;/SKILL.md</c>: name and description are listed to the model.</summary>
    private void AddSkillFolder(Run run, string directory, string label, bool recursive = false, bool builtIn = false)
    {
        IEnumerable<string> files = recursive
            ? SafeFiles(directory, "SKILL.md", recursive: true)
            : SafeDirectories(directory).Select(d => Path.Combine(d, "SKILL.md")).Where(File.Exists);
        foreach (var file in files)
        {
            if (!run.Seen(file) || ReadHead(run, file) is not { } text)
            {
                continue;
            }

            var front = Frontmatter.Parse(text);
            if (front.Get("disable-model-invocation") is { } disabled && disabled.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = front.Get("name") ?? Path.GetFileName(Path.GetDirectoryName(file)) ?? "skill";
            var description = front.Get("description") ?? string.Empty;
            // Codex ships its own skills in skills/.system (recreated by Codex): built in, not the user's.
            var shipped = builtIn || IsCodexSystemSkill(directory, file);
            run.Items.Add(new ContextItem(ContextCategory.Skills, name, file, TokenEstimator.Estimate(name + ": " + description),
                Detail: shipped ? "기본 제공" : label, BuiltIn: shipped));
        }
    }

    private bool IsCodexSystemSkill(string directory, string file) =>
        string.Equals(Path.TrimEndingDirectorySeparator(directory), Path.TrimEndingDirectorySeparator(Path.Combine(CodexHome, "skills")), StringComparison.OrdinalIgnoreCase)
        && Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0].Equals(".system", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>&lt;dir&gt;/*.md</c> sub-agents: name and description are listed to the model.</summary>
    private void AddAgentFolder(Run run, string directory, string label)
    {
        foreach (var file in SafeFiles(directory, "*.md", recursive: false))
        {
            if (!run.Seen(file) || ReadHead(run, file) is not { } text)
            {
                continue;
            }

            var front = Frontmatter.Parse(text);
            var name = front.Get("name") ?? Path.GetFileNameWithoutExtension(file);
            var description = front.Get("description") ?? string.Empty;
            run.Items.Add(new ContextItem(ContextCategory.Agents, name, file, TokenEstimator.Estimate(name + ": " + description), Detail: label));
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string? NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Folders from the top down to <paramref name="folder"/>: every ancestor (Claude), or from the nearest folder with
    /// <c>.git</c> (Codex, agy; just the folder when there is none).
    /// </summary>
    internal static List<string> AncestorsTopDown(string folder, bool stopAtGitRoot)
    {
        var chain = new List<string>();
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            chain.Add(dir.FullName);
            if (stopAtGitRoot && (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))))
            {
                chain.Reverse();
                return chain;
            }
        }

        if (stopAtGitRoot)
        {
            return [folder];
        }

        chain.Reverse();
        return chain;
    }

    private static string DisplayName(string path)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(path));
        return string.IsNullOrEmpty(parent) ? Path.GetFileName(path) : parent + "/" + Path.GetFileName(path);
    }

    private static bool SafeName(string name) => name.Length > 0 && name.IndexOfAny(['/', '\\', ':']) < 0 && name is not "." and not "..";

    private static IEnumerable<string> SafeDirectories(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetDirectories(directory) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeFiles(string directory, string pattern, bool recursive)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, pattern, new EnumerationOptions
                {
                    RecurseSubdirectories = recursive,
                    MaxRecursionDepth = 6,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MatchCasing = MatchCasing.CaseInsensitive,
                })
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? ReadText(Run run, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length > MaxFileBytes)
            {
                run.Problems.Add($"{path}: {MaxFileBytes / 1024 / 1024}MB보다 커서 건너뜀");
                return null;
            }

            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            run.Problems.Add($"{path}: 읽을 수 없음 ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>The first 64 KiB (frontmatter is at the top).</summary>
    private static string? ReadHead(Run run, string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(stream.Length, 64 * 1024)];
            stream.ReadExactly(buffer);
            return Encoding.UTF8.GetString(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            run.Problems.Add($"{path}: 읽을 수 없음 ({ex.GetType().Name})");
            return null;
        }
    }

    [GeneratedRegex(@"`[^`]*`")]
    private static partial Regex InlineCode();

    // "@path" at the start of a line or after whitespace; the path has no spaces (Claude Code's rule).
    [GeneratedRegex(@"(?:^|\s)@((?:~[/\\]|\.{1,2}[/\\]|[A-Za-z]:[/\\]|/)?[^\s@`]+)")]
    private static partial Regex ImportReference();

    private sealed class Run
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public List<ContextItem> Items { get; } = [];

        public List<McpServerSpec> Servers { get; } = [];

        public List<string> Problems { get; } = [];

        /// <summary>Folders to watch, and whether their subfolders are watched too.</summary>
        public Dictionary<string, bool> Watch { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>True the first time a file is met (a file is counted once, however it is reached).</summary>
        public bool Seen(string path)
        {
            try
            {
                return _seen.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        public void WatchDirectory(string directory, bool recursive = false)
        {
            if (Directory.Exists(directory))
            {
                Watch[directory] = recursive || (Watch.TryGetValue(directory, out var known) && known);
            }
        }
    }
}

/// <summary>Minimal YAML frontmatter reader: top-level <c>key: value</c> pairs, quoted values and folded/literal blocks.</summary>
internal sealed class Frontmatter
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Frontmatter Parse(string text)
    {
        var result = new Frontmatter();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return result;
        }

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() == "---")
            {
                break;
            }

            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value is "|" or ">" or "|-" or ">-" or "|+" or ">+" || value.Length == 0)
            {
                var block = new StringBuilder();
                while (i + 1 < lines.Length && lines[i + 1].Trim() != "---" && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
                {
                    i++;
                    block.Append(lines[i].Trim()).Append(value.StartsWith('|') ? '\n' : ' ');
                }

                value = block.ToString().Trim();
            }
            else if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            result._values[key] = value;
        }

        return result;
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public string? Get(string key) => _values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
}
