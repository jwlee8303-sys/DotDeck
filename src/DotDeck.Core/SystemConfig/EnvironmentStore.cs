using System.Text.Json;
using DotDeck.Core.Files;

namespace DotDeck.Core.SystemConfig;

public enum EnvironmentScope
{
    User,
    Machine,
}

/// <summary>Persistent (registry) environment variables of one scope (SPEC §8).</summary>
public interface IEnvironmentStore
{
    /// <summary>True when this process may write <paramref name="scope"/> (Machine needs elevation).</summary>
    bool CanWrite(EnvironmentScope scope);

    /// <summary>Raw (unexpanded) values, sorted by name.</summary>
    IReadOnlyDictionary<string, string> Read(EnvironmentScope scope);

    /// <summary>Sets a value, or deletes it when <paramref name="value"/> is null.</summary>
    void Set(EnvironmentScope scope, string name, string? value);

    /// <summary>Tells running programs that the environment changed (WM_SETTINGCHANGE).</summary>
    void Broadcast();
}

/// <summary>
/// Environment store backed by a JSON file. Used for tests and for sandbox mode (<c>--home</c>), so manual
/// testing never touches the real registry.
/// </summary>
public sealed class FileEnvironmentStore(IFileStore store, string path, bool machineWritable = true) : IEnvironmentStore
{
    public bool CanWrite(EnvironmentScope scope) => scope == EnvironmentScope.User || machineWritable;

    public IReadOnlyDictionary<string, string> Read(EnvironmentScope scope) =>
        Load().TryGetValue(scope.ToString(), out var values) ? new SortedDictionary<string, string>(values, StringComparer.OrdinalIgnoreCase) : new SortedDictionary<string, string>();

    public void Set(EnvironmentScope scope, string name, string? value)
    {
        if (!CanWrite(scope))
        {
            throw new UnauthorizedAccessException("시스템 환경변수는 관리자 권한이 필요합니다.");
        }

        var all = Load();
        if (!all.TryGetValue(scope.ToString(), out var values))
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            all[scope.ToString()] = values;
        }

        if (value is null)
        {
            values.Remove(name);
        }
        else
        {
            values[name] = value;
        }

        store.WriteAllTextAtomic(path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Broadcast()
    {
    }

    private Dictionary<string, Dictionary<string, string>> Load()
    {
        if (!store.FileExists(path))
        {
            return [];
        }

        var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(store.ReadAllText(path)) ?? [];
        return raw.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value, StringComparer.OrdinalIgnoreCase));
    }
}
