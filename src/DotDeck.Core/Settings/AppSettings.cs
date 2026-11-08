using System.Text.Json;
using DotDeck.Core.Files;

namespace DotDeck.Core.Settings;

/// <summary>User preferences, stored in <c>%APPDATA%\DotDeck\settings.json</c>.</summary>
public sealed record AppSettings
{
    public const double MinFontSize = 8;
    public const double MaxFontSize = 32;

    public string Theme { get; init; } = "Dark";

    /// <summary>"화면 공유 모드": mask secret values on screen. Off by default (users see their own values).</summary>
    public bool ScreenShareMode { get; init; }

    public double EditorFontSize { get; init; } = 13.333;

    public int BackupRetention { get; init; } = 50;

    /// <summary>User-chosen backup folder; null uses <c>%LOCALAPPDATA%\DotDeck\backups</c>.</summary>
    public string? BackupDirectory { get; init; }

    /// <summary>Last folder used by "project backup" (remembered for the next time).</summary>
    public string? ProjectBackupDirectory { get; init; }

    /// <summary>
    /// Global default file extensions of the explorer (projects that did not choose their own list use these);
    /// null means the "설정·문서" preset.
    /// </summary>
    public IReadOnlyList<string>? DefaultExtensions { get; init; }

    /// <summary>Clamps out-of-range values (hand-edited files) to safe ones.</summary>
    public AppSettings Normalize() => this with
    {
        Theme = Theme is "Light" ? "Light" : "Dark",
        EditorFontSize = double.IsFinite(EditorFontSize) ? Math.Clamp(EditorFontSize, MinFontSize, MaxFontSize) : 13.333,
        BackupRetention = Math.Clamp(BackupRetention, 1, 1000),
        BackupDirectory = FullOrNull(BackupDirectory),
        ProjectBackupDirectory = FullOrNull(ProjectBackupDirectory),
        DefaultExtensions = DefaultExtensions is null ? null : Explorer.Visibility.ExtensionRules.NormalizeAll(DefaultExtensions),
    };

    private static string? FullOrNull(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path.Trim()) ? path.Trim() : null;
}

public sealed class AppSettingsStore(IFileStore store, string path)
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    /// <summary>Loads settings; a missing or unreadable file gives the defaults.</summary>
    public AppSettings Load()
    {
        try
        {
            return store.FileExists(path)
                ? (JsonSerializer.Deserialize<AppSettings>(store.ReadAllText(path)) ?? new AppSettings()).Normalize()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        store.CreateDirectory(Path.GetDirectoryName(path)!);
        store.WriteAllTextAtomic(path, JsonSerializer.Serialize(settings.Normalize(), s_json));
    }
}
