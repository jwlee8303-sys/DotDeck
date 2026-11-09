using DotDeck.Core.Files;
using DotDeck.Core.Platform;

namespace DotDeck.Core.AiCli.Launch;

public sealed record CliLocatorResult(CliLocation? Location, string? Error);

/// <summary>
/// Finds the Claude Code / Codex executable. Searches PATH (absolute entries only, never the current folder, so a
/// project cannot plant its own <c>claude.exe</c>) and refuses <c>.cmd</c>/<c>.bat</c> launchers because cmd.exe would
/// re-parse the arguments; npm shims are replaced by <c>node.exe &lt;package script&gt;</c>.
/// </summary>
public sealed class CliLocator(IProcessEnvironment environment, IFileStore store)
{
    private static readonly string[] DefaultPathExt = [".COM", ".EXE", ".BAT", ".CMD"];

    public CliLocatorResult Find(CliKind cli, string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!Path.IsPathFullyQualified(overridePath) || !overridePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !store.FileExists(overridePath))
            {
                return new(null, $"지정한 CLI 경로가 올바른 .exe가 아닙니다: {overridePath}");
            }

            return new(new CliLocation(cli, overridePath, []), null);
        }

        var name = cli == CliKind.Claude ? "claude" : "codex";
        string? shim = null;
        foreach (var dir in PathDirectories())
        {
            foreach (var ext in PathExtensions())
            {
                var candidate = Path.Combine(dir, name + ext);
                if (!store.FileExists(candidate))
                {
                    continue;
                }

                if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".com", StringComparison.OrdinalIgnoreCase))
                {
                    return new(new CliLocation(cli, candidate, []), null);
                }

                if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
                {
                    var script = NpmScript(cli, dir, candidate);
                    if (script is not null)
                    {
                        var node = FindExe("node");
                        if (node is not null)
                        {
                            return new(new CliLocation(cli, node, [script]), null);
                        }
                    }

                    shim ??= candidate;
                }
            }
        }

        return shim is null
            ? new(null, $"{name} 실행 파일을 PATH에서 찾을 수 없습니다. 설정에서 CLI 경로를 지정하세요.")
            : new(null, $"{shim}는 배치 파일이라 안전하게 실행할 수 없습니다. 설정에서 .exe 경로를 지정하세요.");
    }

    /// <summary>
    /// The package script an npm shim runs, only when the batch file itself names that script (npm writes
    /// <c>"%dp0%\node_modules\@openai\codex\bin\codex.js"</c>); any other batch file is refused, not substituted.
    /// </summary>
    private string? NpmScript(CliKind cli, string shimDirectory, string shimPath)
    {
        var relative = cli == CliKind.Codex
            ? Path.Combine("node_modules", "@openai", "codex", "bin", "codex.js")
            : Path.Combine("node_modules", "@anthropic-ai", "claude-code", "cli.js");
        var script = Path.Combine(shimDirectory, relative);
        if (!store.FileExists(script))
        {
            return null;
        }

        string text;
        try
        {
            text = System.Text.Encoding.UTF8.GetString(store.ReadHead(shimPath, 64 * 1024));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return text.Contains("%dp0%\\" + relative, StringComparison.OrdinalIgnoreCase)
            || text.Contains("%~dp0\\" + relative, StringComparison.OrdinalIgnoreCase)
            ? script
            : null;
    }

    private string? FindExe(string name)
    {
        foreach (var dir in PathDirectories())
        {
            var candidate = Path.Combine(dir, name + ".exe");
            if (store.FileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IEnumerable<string> PathDirectories() =>
        (environment.GetVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim('"'))
            .Where(Path.IsPathFullyQualified)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private IEnumerable<string> PathExtensions()
    {
        var configured = (environment.GetVariable("PATHEXT") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => e.StartsWith('.'))
            .ToList();
        return configured.Count == 0 ? DefaultPathExt : configured;
    }
}
