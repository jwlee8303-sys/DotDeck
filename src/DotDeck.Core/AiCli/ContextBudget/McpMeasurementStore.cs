using System.Text.Json;
using DotDeck.Core.Files;

namespace DotDeck.Core.AiCli.ContextBudget;

/// <summary>
/// One measured server. Only these fields are stored: no command, argument, env, header or server output.
/// <paramref name="Key"/> is the configuration file path and the server name (<see cref="McpServerSpec.Key"/>).
/// </summary>
public sealed record McpMeasurement(string Key, string Name, int ToolCount, int Tokens, DateTimeOffset MeasuredAt, string ConfigHash);

/// <summary>
/// Measurements in <c>%LOCALAPPDATA%\DotDeck\state\context-mcp.json</c>. A stored measurement is also the user's
/// approval to run that server: it is valid only while the server's <see cref="McpServerSpec.ConfigHash"/> is the same,
/// so a changed configuration needs a new confirmation and a new measurement.
/// </summary>
public sealed class McpMeasurementStore(IFileStore store, string path)
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private readonly Lock _gate = new();
    private Dictionary<string, McpMeasurement>? _entries;

    /// <summary>The measurement for the server as currently configured, or null (never measured, or configuration changed).</summary>
    public McpMeasurement? Find(McpServerSpec server)
    {
        ArgumentNullException.ThrowIfNull(server);
        lock (_gate)
        {
            return Load().TryGetValue(server.Key, out var found) && found.ConfigHash == server.ConfigHash ? found : null;
        }
    }

    /// <summary>A measurement exists but for another configuration (shown as "설정 변경됨").</summary>
    public bool IsStale(McpServerSpec server)
    {
        ArgumentNullException.ThrowIfNull(server);
        lock (_gate)
        {
            return Load().TryGetValue(server.Key, out var found) && found.ConfigHash != server.ConfigHash;
        }
    }

    public void Save(McpMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        lock (_gate)
        {
            var entries = Load();
            entries[measurement.Key] = measurement;
            try
            {
                store.CreateDirectory(Path.GetDirectoryName(path)!);
                store.WriteAllTextAtomic(path, JsonSerializer.Serialize(entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal), s_json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The measurement still shows for this run.
            }
        }
    }

    private Dictionary<string, McpMeasurement> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        _entries = new Dictionary<string, McpMeasurement>(StringComparer.Ordinal);
        try
        {
            if (store.FileExists(path)
                && JsonSerializer.Deserialize<List<McpMeasurement>>(store.ReadAllText(path)) is { } list)
            {
                foreach (var entry in list.Where(e => e is { Key: not null, ConfigHash: not null, Name: not null }))
                {
                    _entries[entry.Key] = entry;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return _entries;
    }
}
