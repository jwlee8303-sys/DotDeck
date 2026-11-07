using DotDeck.Core.Platform;
using SpecialFolder = System.Environment.SpecialFolder;

namespace DotDeck.Core.Paths;

/// <summary>
/// Resolves <see cref="AppPaths"/> from command-line arguments and the process environment.
/// Home priority: <c>--home</c> &gt; <c>DOTDECK_HOME</c> &gt; user profile.
/// When the home is overridden, app data is isolated under <c>&lt;home&gt;\.dotdeck</c> so that
/// sandbox runs never touch the real %APPDATA% / %LOCALAPPDATA% data. <c>--data</c> overrides data location.
/// </summary>
public static class AppPathsResolver
{
    public const string HomeVariable = "DOTDECK_HOME";
    public const string HomeOption = "--home";
    public const string DataOption = "--data";
    public const string SandboxDataFolderName = ".dotdeck";

    public static AppPaths Resolve(IReadOnlyList<string> args, IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);

        var baseDir = environment.CurrentDirectory;
        var homeArg = FindOption(args, HomeOption);
        var dataArg = FindOption(args, DataOption);

        string home;
        HomeSource source;
        if (homeArg is not null)
        {
            home = Normalize(homeArg, baseDir, HomeOption);
            source = HomeSource.CommandLine;
        }
        else if (!string.IsNullOrWhiteSpace(environment.GetVariable(HomeVariable)))
        {
            home = Normalize(environment.GetVariable(HomeVariable)!, baseDir, HomeVariable);
            source = HomeSource.EnvironmentVariable;
        }
        else
        {
            home = Normalize(environment.GetFolderPath(SpecialFolder.UserProfile), baseDir, "USERPROFILE");
            source = HomeSource.UserProfile;
        }

        string roaming;
        string local;
        if (dataArg is not null)
        {
            var dataRoot = Normalize(dataArg, baseDir, DataOption);
            roaming = Path.Combine(dataRoot, "roaming");
            local = Path.Combine(dataRoot, "local");
        }
        else if (source != HomeSource.UserProfile)
        {
            var dataRoot = Path.Combine(home, SandboxDataFolderName);
            roaming = Path.Combine(dataRoot, "roaming");
            local = Path.Combine(dataRoot, "local");
        }
        else
        {
            roaming = Path.Combine(
                Normalize(environment.GetFolderPath(SpecialFolder.ApplicationData), baseDir, "APPDATA"),
                AppPaths.AppFolderName);
            local = Path.Combine(
                Normalize(environment.GetFolderPath(SpecialFolder.LocalApplicationData), baseDir, "LOCALAPPDATA"),
                AppPaths.AppFolderName);
        }

        return new AppPaths(home, source, roaming, local);
    }

    /// <summary>Supports <c>--name value</c> and <c>--name=value</c>. Last occurrence wins.</summary>
    private static string? FindOption(IReadOnlyList<string> args, string name)
    {
        string? value = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new AppPathsException($"Option '{name}' requires a directory value.");
                }

                value = args[++i];
            }
            else if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg[(name.Length + 1)..];
            }
        }

        return value;
    }

    private static string Normalize(string path, string baseDir, string sourceName)
    {
        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            throw new AppPathsException($"'{sourceName}' is empty.");
        }

        try
        {
            var full = Path.GetFullPath(trimmed, baseDir);
            return Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AppPathsException($"'{sourceName}' is not a valid path: {trimmed}", ex);
        }
    }
}

public sealed class AppPathsException : Exception
{
    public AppPathsException(string message)
        : base(message)
    {
    }

    public AppPathsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
