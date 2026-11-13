namespace DotDeck.Core.Paths;

public enum HomeSource
{
    UserProfile,
    EnvironmentVariable,
    CommandLine,
}

/// <summary>
/// Resolved locations used by the app. Built-in projects are rooted under <see cref="Home"/>;
/// app data (projects.json, backups, logs, layouts) lives under the two data directories.
/// </summary>
public sealed record AppPaths(string Home, HomeSource HomeSource, string RoamingDataDir, string LocalDataDir)
{
    public const string AppFolderName = "DotDeck";

    /// <summary>True when the home directory is not the real user profile (test/sandbox mode).</summary>
    public bool IsHomeOverridden => HomeSource != HomeSource.UserProfile;

    public string ClaudeHome => Path.Combine(Home, ".claude");

    public string CodexHome => Path.Combine(Home, ".codex");

    public string GeminiHome => Path.Combine(Home, ".gemini");

    public string ProjectsFile => Path.Combine(RoamingDataDir, "projects.json");

    public string SettingsFile => Path.Combine(RoamingDataDir, "settings.json");

    public string ExclusionsFile => Path.Combine(RoamingDataDir, "exclusions.json");

    public string TemplatesDir => Path.Combine(RoamingDataDir, "templates");

    /// <summary>"다음 형식으로 열기" choices per file.</summary>
    public string EditorOverridesFile => Path.Combine(RoamingDataDir, "editor-overrides.json");

    /// <summary>User copies of the built-in catalogs (<c>&lt;product&gt;\&lt;name&gt;.json</c>) that replace them.</summary>
    public string CatalogsDir => Path.Combine(RoamingDataDir, "catalogs");

    /// <summary>AI CLI session presets, last selections and conversation ids (SPEC 11.4).</summary>
    public string AiSessionsFile => Path.Combine(RoamingDataDir, "sessions.json");

    /// <summary>Per-session working files of AI CLI sessions (scope.json, settings.json, context.md, …).</summary>
    public string AiSessionsDir => Path.Combine(LocalDataDir, "sessions");

    public string BackupsDir => Path.Combine(LocalDataDir, "backups");

    public string LogsDir => Path.Combine(LocalDataDir, "logs");

    public string LayoutDir => Path.Combine(LocalDataDir, "layout");

    public string StateDir => Path.Combine(LocalDataDir, "state");

    /// <summary>Explorer expansion/selection (and later open documents), DESIGN 4.</summary>
    public string SessionFile => Path.Combine(StateDir, "session.json");

    public string WebView2DataDir => Path.Combine(LocalDataDir, "WebView2");
}
