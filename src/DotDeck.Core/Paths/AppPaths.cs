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

    public string ProjectsFile => Path.Combine(RoamingDataDir, "projects.json");

    public string SettingsFile => Path.Combine(RoamingDataDir, "settings.json");

    public string TemplatesDir => Path.Combine(RoamingDataDir, "templates");

    public string BackupsDir => Path.Combine(LocalDataDir, "backups");

    public string LogsDir => Path.Combine(LocalDataDir, "logs");

    public string LayoutDir => Path.Combine(LocalDataDir, "layout");

    public string StateDir => Path.Combine(LocalDataDir, "state");

    /// <summary>Explorer expansion/selection (and later open documents), DESIGN 4.</summary>
    public string SessionFile => Path.Combine(StateDir, "session.json");

    public string WebView2DataDir => Path.Combine(LocalDataDir, "WebView2");
}
