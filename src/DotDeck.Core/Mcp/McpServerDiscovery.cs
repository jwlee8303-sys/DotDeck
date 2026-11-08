using System.Text.Json;
using System.Text.RegularExpressions;
using DotDeck.Core.Files;

namespace DotDeck.Core.Mcp;

/// <summary>
/// An MCP server found in a configuration file. Only the name and where it came from are kept: server entries may
/// hold API keys (env, headers, args), which never leave the parser.
/// </summary>
/// <param name="Name">Server name as Claude Code uses it in tool names (<c>.</c> and spaces become <c>_</c>).</param>
/// <param name="Source">Korean label, e.g. "사용자 (~/.claude.json)".</param>
public sealed record McpServerInfo(string Name, string Source, string SourcePath)
{
    /// <summary>Permission rule for all of the server's tools.</summary>
    public string PermissionRule => $"mcp__{Name}";

    /// <summary>Hook matcher for all of the server's tools.</summary>
    public string MatcherPattern => $"mcp__{Name}__.*";
}

/// <param name="Problems">Files that exist but could not be read (path and a short reason, never file content).</param>
public sealed record McpDiscoveryResult(IReadOnlyList<McpServerInfo> Servers, IReadOnlyList<string> Problems);

/// <summary>
/// Finds configured MCP server names by reading configuration files (read-only; servers are never started or
/// contacted). Sources follow https://code.claude.com/docs/en/mcp, /plugins-reference and /managed-mcp (2026-11-08):
/// user and local scope in <c>~/.claude.json</c> (top-level <c>mcpServers</c>, <c>projects[path].mcpServers</c>),
/// project scope in <c>&lt;project&gt;/.mcp.json</c>, installed plugins under <c>~/.claude/plugins/cache</c>, and the
/// managed <c>managed-mcp.json</c>. claude.ai connectors are not stored locally and cannot be found.
/// </summary>
public sealed partial class McpServerDiscovery(IFileStore store, string home, string? managedMcpPath = null)
{
    public const long MaxFileBytes = 32L * 1024 * 1024;

    private static readonly JsonDocumentOptions s_read = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <param name="projectRoot">Root of the project whose settings are edited; null for user settings.</param>
    public McpDiscoveryResult Discover(string? projectRoot)
    {
        var servers = new List<McpServerInfo>();
        var problems = new List<string>();
        var claudeJson = Path.Combine(home, ".claude.json");
        Read(claudeJson, problems, root =>
        {
            AddServers(root, "사용자 (~/.claude.json)", claudeJson, servers);
            if (projectRoot is not null && root.TryGetProperty("projects", out var projects) && projects.ValueKind == JsonValueKind.Object)
            {
                foreach (var project in projects.EnumerateObject())
                {
                    if (SamePath(project.Name, projectRoot))
                    {
                        AddServers(project.Value, "로컬 (~/.claude.json · 이 프로젝트)", claudeJson, servers);
                    }
                }
            }
        });

        if (projectRoot is not null)
        {
            var mcpJson = Path.Combine(projectRoot, ".mcp.json");
            Read(mcpJson, problems, root => AddServers(root, "프로젝트 (.mcp.json)", mcpJson, servers));
        }

        DiscoverPlugins(servers, problems);

        if (managedMcpPath is not null)
        {
            Read(managedMcpPath, problems, root => AddServers(root, "관리형 (managed-mcp.json)", managedMcpPath, servers));
        }

        // One entry per name and source (a plugin can be cached in several versions).
        var unique = servers.DistinctBy(s => (s.Name, s.Source)).ToList();
        return new McpDiscoveryResult(unique, problems);
    }

    /// <summary><c>~/.claude/plugins/cache/&lt;marketplace&gt;/&lt;plugin&gt;/&lt;version&gt;/</c>: root <c>.mcp.json</c> and <c>.claude-plugin/plugin.json</c>.</summary>
    private void DiscoverPlugins(List<McpServerInfo> servers, List<string> problems)
    {
        var cache = Path.Combine(home, ".claude", "plugins", "cache");
        if (!store.DirectoryExists(cache))
        {
            return;
        }

        foreach (var marketplace in Folders(cache))
        {
            foreach (var plugin in Folders(marketplace.FullPath))
            {
                foreach (var version in Folders(plugin.FullPath))
                {
                    var label = $"플러그인 {plugin.Name}@{marketplace.Name} (설치됨, 사용 여부는 enabledPlugins 확인)";
                    var prefix = $"plugin_{NormalizePlugin(plugin.Name)}_";
                    foreach (var file in new[] { Path.Combine(version.FullPath, ".mcp.json"), Path.Combine(version.FullPath, ".claude-plugin", "plugin.json") })
                    {
                        Read(file, problems, root =>
                        {
                            var found = new List<McpServerInfo>();
                            AddServers(root, label, file, found, NormalizePlugin);
                            servers.AddRange(found.Select(s => s with { Name = prefix + s.Name }));
                        });
                    }
                }
            }
        }
    }

    private IEnumerable<FileSystemEntry> Folders(string directory)
    {
        try
        {
            return store.GetChildren(directory).Where(e => e.IsDirectory && !e.IsReparsePoint).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void AddServers(JsonElement owner, string source, string path, List<McpServerInfo> servers, Func<string, string>? normalize = null)
    {
        normalize ??= Normalize;
        if (owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("mcpServers", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var server in map.EnumerateObject())
            {
                if (server.Name.Trim().Length > 0)
                {
                    servers.Add(new McpServerInfo(normalize(server.Name), source, path));
                }
            }
        }
    }

    private void Read(string path, List<string> problems, Action<JsonElement> use)
    {
        try
        {
            if (store.GetEntry(path) is not { IsDirectory: false } entry)
            {
                return;
            }

            if (entry.Length > MaxFileBytes)
            {
                problems.Add($"파일이 너무 커서 건너뜀: {path}");
                return;
            }

            using var document = JsonDocument.Parse(store.ReadAllBytes(path), s_read);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"JSON 최상위가 객체가 아님: {path}");
                return;
            }

            use(document.RootElement);
        }
        catch (JsonException)
        {
            // The message could quote file content: report only the path.
            problems.Add($"JSON 형식이 올바르지 않음: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"읽을 수 없음: {path}");
        }
    }

    /// <summary>Claude Code replaces <c>.</c> and spaces in server names with <c>_</c> in tool names.</summary>
    public static string Normalize(string name) => name.Trim().Replace('.', '_').Replace(' ', '_');

    /// <summary>
    /// Plugin servers: "any character outside A-Z, a-z, 0-9, _ and - is replaced with _" in both the plugin name and
    /// the server key (https://code.claude.com/docs/en/mcp, "Plugin MCP tool names").
    /// </summary>
    public static string NormalizePlugin(string name) => NotToolNameChar().Replace(name.Trim(), "_");

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex NotToolNameChar();

    /// <summary><c>~/.claude.json</c> project keys may use <c>/</c> or <c>\</c>; compared without case or trailing separators.</summary>
    internal static bool SamePath(string key, string path)
    {
        static string Clean(string p) => p.Replace('/', '\\').TrimEnd('\\');
        return string.Equals(Clean(key), Clean(path), StringComparison.OrdinalIgnoreCase);
    }
}
