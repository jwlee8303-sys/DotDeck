using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Platform;

namespace DotDeck.Core.AiCli.Launch;

/// <summary>The PowerShell used for plain terminals (SPEC 11.3-4).</summary>
public sealed record ShellLocation(string Executable, bool IsWindowsPowerShell)
{
    public string DisplayName => IsWindowsPowerShell ? "Windows PowerShell" : "PowerShell";
}

/// <summary>
/// Plain PowerShell terminals (DESIGN A2): PATH <c>pwsh.exe</c> (absolute entries only) →
/// <c>%ProgramFiles%\PowerShell\7\pwsh.exe</c> → Windows PowerShell 5.1, started with UTF-8 console encodings.
/// </summary>
public static class ShellTerminal
{
    /// <summary>Makes native programs' piped output and input UTF-8, matching the pseudo console.</summary>
    public const string Utf8Initialization =
        "[Console]::InputEncoding = [Console]::OutputEncoding = $OutputEncoding = [System.Text.UTF8Encoding]::new($false)";

    // Preserve profile prompts while reporting FileSystem locations through OSC 7 on every prompt.
    public const string PromptInitialization =
        "$global:DotDeckOriginalPrompt = $function:prompt; function global:prompt { "
        + "if ($PWD.Provider.Name -eq 'FileSystem') { "
        + "[Console]::Write(([string][char]27 + ']7;' + ([uri]$PWD.ProviderPath).AbsoluteUri + [char]7)) }; "
        + "& $global:DotDeckOriginalPrompt }";

    public static ShellLocation? Locate(IProcessEnvironment environment, IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(store);

        foreach (var dir in (environment.GetVariable("PATH") ?? string.Empty)
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(d => d.Trim('"'))
                     .Where(Path.IsPathFullyQualified))
        {
            var candidate = Path.Combine(dir, "pwsh.exe");
            if (store.FileExists(candidate))
            {
                return new ShellLocation(candidate, IsWindowsPowerShell: false);
            }
        }

        var programFiles = environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            var installed = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
            if (store.FileExists(installed))
            {
                return new ShellLocation(installed, IsWindowsPowerShell: false);
            }
        }

        var system = environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(system))
        {
            var legacy = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (store.FileExists(legacy))
            {
                return new ShellLocation(legacy, IsWindowsPowerShell: true);
            }
        }

        return null;
    }

    /// <summary>
    /// The launch plan of a plain terminal in <paramref name="workingDirectory"/>. In sandbox mode (<c>--home</c>)
    /// the CLIs started from it use the sandbox homes too (DESIGN A4); otherwise the environment is inherited as is.
    /// </summary>
    public static CliLaunchPlan Plan(ShellLocation shell, string workingDirectory, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(paths);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (paths.IsHomeOverridden)
        {
            environment["CLAUDE_CONFIG_DIR"] = paths.ClaudeHome;
            environment["CODEX_HOME"] = paths.CodexHome;
        }

        return new CliLaunchPlan
        {
            Executable = shell.Executable,
            Arguments = ["-NoLogo", "-NoExit", "-Command", Utf8Initialization + "; " + PromptInitialization],
            WorkingDirectory = workingDirectory,
            Environment = environment,
        };
    }
}
