using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotDeck.Core.AiCli.ContextBudget;

/// <summary>
/// Reads MCP server entries into <see cref="McpServerSpec"/>s. Values (env, headers, args) stay in the specs only to be
/// handed to a measurement; problems name the file and the error type, never content.
/// </summary>
internal static partial class McpConfigReader
{
    private const long MaxBytes = 32L * 1024 * 1024;

    private static readonly JsonDocumentOptions s_json = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Claude Code: user scope (<c>~/.claude.json</c> <c>mcpServers</c>), local scope (<c>projects[&lt;folder&gt;]</c> of the
    /// deepest folder of <paramref name="chain"/> that has an entry) and project scope (<c>.mcp.json</c> in the chain).
    /// <c>disabledMcpServers</c> and <c>disabledMcpjsonServers</c> of that entry mark servers off.
    /// </summary>
    public static void AddClaude(List<McpServerSpec> servers, List<string> problems, string home, IReadOnlyList<string> chain)
    {
        var claudeJson = Path.Combine(home, ".claude.json");
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        var disabledProject = new HashSet<string>(StringComparer.Ordinal);
        Read(claudeJson, problems, root =>
        {
            JsonElement? project = null;
            if (root.TryGetProperty("projects", out var projects) && projects.ValueKind == JsonValueKind.Object)
            {
                var best = -1;
                foreach (var entry in projects.EnumerateObject())
                {
                    for (var i = chain.Count - 1; i > best; i--)
                    {
                        if (SamePath(entry.Name, chain[i]))
                        {
                            best = i;
                            project = entry.Value;
                            break;
                        }
                    }
                }
            }

            if (project is { } p)
            {
                Names(p, "disabledMcpServers", disabled);
                Names(p, "disabledMcpjsonServers", disabledProject);
            }

            AddMap(servers, root, "사용자 (~/.claude.json)", claudeJson, null, disabled);
            if (project is { } local)
            {
                AddMap(servers, local, "로컬 (~/.claude.json · 이 프로젝트)", claudeJson, null, disabled);
            }
        });

        foreach (var dir in chain)
        {
            var mcpJson = Path.Combine(dir, ".mcp.json");
            if (File.Exists(mcpJson))
            {
                Read(mcpJson, problems, root => AddMap(servers, root, "프로젝트 (.mcp.json)", mcpJson, null, new HashSet<string>([.. disabled, .. disabledProject], StringComparer.Ordinal)));
            }
        }
    }

    /// <summary>A JSON file with a top-level <c>mcpServers</c> object (plugin <c>.mcp.json</c>, agy <c>mcp_config.json</c>).</summary>
    public static void AddJsonServers(
        List<McpServerSpec> servers, List<string> problems, string path, string source,
        ISet<string>? disabled, IReadOnlyDictionary<string, string>? variables)
    {
        if (File.Exists(path))
        {
            Read(path, problems, root => AddMap(servers, root, source, path, variables, disabled ?? new HashSet<string>()));
        }
    }

    /// <summary><c>enabledPlugins</c> entries set to true ("plugin@marketplace").</summary>
    public static List<string> EnabledPlugins(string settingsPath, List<string> problems)
    {
        var result = new List<string>();
        if (File.Exists(settingsPath))
        {
            Read(settingsPath, problems, root =>
            {
                if (root.TryGetProperty("enabledPlugins", out var map) && map.ValueKind == JsonValueKind.Object)
                {
                    result.AddRange(map.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.True).Select(p => p.Name));
                }
            });
        }

        return result;
    }

    /// <summary>Codex <c>[mcp_servers.&lt;name&gt;]</c> tables.</summary>
    public static void AddCodexToml(List<McpServerSpec> servers, List<string> problems, string path, string source)
    {
        if (!File.Exists(path))
        {
            return;
        }

        Tomlyn.Model.TomlTable model;
        try
        {
            if (new FileInfo(path).Length > MaxBytes)
            {
                problems.Add($"{path}: 너무 커서 건너뜀");
                return;
            }

            model = Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(File.ReadAllText(path))!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{path}: 읽을 수 없음 ({ex.GetType().Name})");
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problems.Add($"{path}: TOML 형식 오류 ({ex.GetType().Name})");
            return;
        }

        if (model is null || !model.TryGetValue("mcp_servers", out var raw) || raw is not Tomlyn.Model.TomlTable table)
        {
            return;
        }

        foreach (var (name, value) in table)
        {
            if (value is not Tomlyn.Model.TomlTable server)
            {
                continue;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, header) in TomlMap(server, "http_headers"))
            {
                headers[key] = header;
            }

            foreach (var (key, variable) in TomlMap(server, "env_http_headers"))
            {
                if (Environment.GetEnvironmentVariable(variable) is { } headerValue)
                {
                    headers[key] = headerValue;
                }
            }

            if (TomlString(server, "bearer_token_env_var") is { } tokenVariable && Environment.GetEnvironmentVariable(tokenVariable) is { } token)
            {
                headers["Authorization"] = "Bearer " + token;
            }

            var url = TomlString(server, "url");
            servers.Add(new McpServerSpec
            {
                Name = name,
                Source = source,
                SourcePath = path,
                Transport = url is null ? McpTransport.Stdio : McpTransport.Http,
                Disabled = server.TryGetValue("enabled", out var enabled) && enabled is false,
                Command = TomlString(server, "command"),
                Arguments = server.TryGetValue("args", out var args) && args is Tomlyn.Model.TomlArray array
                    ? [.. array.OfType<string>()]
                    : [],
                Environment = TomlMap(server, "env"),
                WorkingDirectory = TomlString(server, "cwd"),
                Url = url,
                Headers = headers,
                UsesOAuth = TomlString(server, "auth") is { } auth && auth.Equals("oauth", StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    private static string? TomlString(Tomlyn.Model.TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is string text && text.Length > 0 ? text : null;

    private static Dictionary<string, string> TomlMap(Tomlyn.Model.TomlTable table, string key)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (table.TryGetValue(key, out var value) && value is Tomlyn.Model.TomlTable inner)
        {
            foreach (var (name, item) in inner)
            {
                if (item is string text)
                {
                    map[name] = text;
                }
            }
        }

        return map;
    }

    private static void AddMap(
        List<McpServerSpec> servers, JsonElement owner, string source, string path,
        IReadOnlyDictionary<string, string>? variables, ISet<string> disabled)
    {
        if (!owner.TryGetProperty("mcpServers", out var map) || map.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var server in map.EnumerateObject())
        {
            if (server.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var entry = server.Value;
            string? Text(string key) => entry.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? Expand(v.GetString()!, variables) : null;
            Dictionary<string, string> Map(string key)
            {
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                if (entry.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object)
                {
                    foreach (var item in v.EnumerateObject())
                    {
                        if (item.Value.ValueKind == JsonValueKind.String)
                        {
                            result[item.Name] = Expand(item.Value.GetString()!, variables);
                        }
                    }
                }

                return result;
            }

            var url = Text("url") ?? Text("serverUrl") ?? Text("httpUrl");
            var type = Text("type")?.ToLowerInvariant();
            var transport = type switch
            {
                "sse" => McpTransport.Sse,
                "http" or "streamable-http" or "streamablehttp" => McpTransport.Http,
                "stdio" => McpTransport.Stdio,
                _ => url is null ? McpTransport.Stdio : McpTransport.Http,
            };
            servers.Add(new McpServerSpec
            {
                Name = server.Name,
                Source = source,
                SourcePath = path,
                Transport = transport,
                Disabled = disabled.Contains(server.Name) || (entry.TryGetProperty("disabled", out var off) && off.ValueKind == JsonValueKind.True),
                Unsupported = entry.TryGetProperty("headersHelper", out _) ? "headersHelper를 쓰는 서버는 측정하지 않음" : null,
                Command = Text("command"),
                Arguments = entry.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array
                    ? [.. args.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => Expand(a.GetString()!, variables))]
                    : [],
                Environment = Map("env"),
                WorkingDirectory = Text("cwd"),
                Url = url,
                Headers = Map("headers"),
                UsesOAuth = entry.TryGetProperty("oauth", out var oauth) && oauth.ValueKind is JsonValueKind.Object or JsonValueKind.True,
            });
        }
    }

    /// <summary><c>${VAR}</c> and <c>${VAR:-default}</c> from <paramref name="variables"/>, then the process environment.</summary>
    internal static string Expand(string value, IReadOnlyDictionary<string, string>? variables) =>
        Variable().Replace(value, match =>
        {
            var name = match.Groups[1].Value;
            if (variables is not null && variables.TryGetValue(name, out var known))
            {
                return known;
            }

            return Environment.GetEnvironmentVariable(name) ?? (match.Groups[2].Success ? match.Groups[2].Value : string.Empty);
        });

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}")]
    private static partial Regex Variable();

    private static void Names(JsonElement owner, string key, HashSet<string> into)
    {
        if (owner.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    into.Add(item.GetString()!);
                }
            }
        }
    }

    private static void Read(string path, List<string> problems, Action<JsonElement> use)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            if (new FileInfo(path).Length > MaxBytes)
            {
                problems.Add($"{path}: 너무 커서 건너뜀");
                return;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                return;
            }

            using var document = JsonDocument.Parse(bytes, s_json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                use(document.RootElement);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{path}: 읽을 수 없음 ({ex.GetType().Name})");
        }
        catch (JsonException)
        {
            problems.Add($"{path}: JSON 형식 오류");
        }
    }

    private static bool SamePath(string a, string b)
    {
        static string Norm(string p) => Path.TrimEndingDirectorySeparator(p.Replace('/', '\\'));
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }
}
