using System.Text.RegularExpressions;
using DotDeck.Core.Roles;

namespace DotDeck.Core.Editors;

public enum HookScriptStatus
{
    /// <summary>The command names no script file (e.g. an inline shell command).</summary>
    NoScript,
    Exists,
    Missing,

    /// <summary>The path depends on something only known when Claude Code runs (shown as "확인 불가").</summary>
    Unknown,
}

/// <param name="Path">The resolved script path (or the raw token when it cannot be resolved).</param>
/// <param name="Reason">Why the path cannot be checked, or a note such as "추정".</param>
public sealed record HookScriptCheck(HookScriptStatus Status, string? Path = null, string? Reason = null);

/// <summary>Where a settings file sits; lets hook paths be resolved like Claude Code would.</summary>
/// <param name="ProjectRoot">The project root for project and local settings; null for user settings.</param>
public sealed record SettingsContext(ConfigScope Scope, string Home, string? ProjectRoot)
{
    public static SettingsContext? From(FileRole? role, string home) =>
        role is null ? null : new SettingsContext(role.Scope, home, role.ProjectRoot);
}

/// <summary>
/// Finds the script a hook command runs and checks that it exists, resolving <c>$CLAUDE_PROJECT_DIR</c>, the home
/// folder and relative paths from the settings file's location. Hooks run in Claude Code's working folder, which for
/// project settings is normally the project root, so relative paths there are checked as an estimate.
/// </summary>
public static partial class HookScriptResolver
{
    private static readonly HashSet<string> s_launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "python3", "py", "node", "bun", "deno", "bash", "sh", "zsh", "pwsh", "powershell", "cmd", "uv", "ruby", "perl",
        "python.exe", "python3.exe", "py.exe", "node.exe", "bash.exe", "pwsh.exe", "powershell.exe", "cmd.exe", "uv.exe", "run",
    };

    public static HookScriptCheck Check(string command, IReadOnlyList<string>? args, SettingsContext? context, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(fileExists);
        var tokens = Tokenize(command).Concat(args ?? []).Take(6);
        foreach (var token in tokens)
        {
            if (token.Length == 0 || IsLauncher(token) || token.StartsWith('-') || IsSwitch(token))
            {
                continue;
            }

            if (!LooksLikeScript(token))
            {
                // The first real word is a program name such as "echo" or "npx": nothing to check.
                return new HookScriptCheck(HookScriptStatus.NoScript);
            }

            try
            {
                return Resolve(token, context, fileExists);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new HookScriptCheck(HookScriptStatus.Unknown, token, "경로 형식을 해석할 수 없습니다.");
            }
        }

        return new HookScriptCheck(HookScriptStatus.NoScript);
    }

    /// <summary>An interpreter, also when given by path (<c>"C:\Python\python.exe"</c>, <c>/usr/bin/env</c>).</summary>
    private static bool IsLauncher(string token)
    {
        if (s_launchers.Contains(token))
        {
            return true;
        }

        var name = token.Replace('/', '\\');
        var slash = name.LastIndexOf('\\');
        name = slash >= 0 ? name[(slash + 1)..] : name;
        return slash >= 0 && (s_launchers.Contains(name) || name.Equals("env", StringComparison.OrdinalIgnoreCase));
    }

    private static HookScriptCheck Resolve(string token, SettingsContext? context, Func<string, bool> fileExists)
    {
        var text = token;
        string? unknown = null;
        text = VariablePattern().Replace(text, m =>
        {
            var name = m.Groups["a"].Success ? m.Groups["a"].Value
                : m.Groups["b"].Success ? m.Groups["b"].Value
                : m.Groups["c"].Success ? m.Groups["c"].Value
                : m.Groups["d"].Value;
            switch (name.ToUpperInvariant())
            {
                case "CLAUDE_PROJECT_DIR":
                    if (context?.ProjectRoot is { } root)
                    {
                        return root;
                    }

                    unknown ??= "사용자 설정의 $CLAUDE_PROJECT_DIR는 Claude Code를 실행한 프로젝트에 따라 달라집니다.";
                    return m.Value;
                case "HOME" or "USERPROFILE" when context is not null:
                    return context.Home;
                default:
                    unknown ??= $"변수 {name}의 값은 실행할 때 정해집니다.";
                    return m.Value;
            }
        });

        if (unknown is not null)
        {
            return new HookScriptCheck(HookScriptStatus.Unknown, token, unknown);
        }

        if (context is null)
        {
            return Path.IsPathFullyQualified(text)
                ? Exists(text, fileExists, null)
                : new HookScriptCheck(HookScriptStatus.Unknown, token, "설정 파일의 위치를 알 수 없습니다.");
        }

        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith(@"~\", StringComparison.Ordinal))
        {
            text = Path.Combine(context.Home, text.Length > 2 ? text[2..] : string.Empty);
        }

        // Git Bash form: /c/Users/... → C:\Users\...
        if (GitBashDrive().Match(text) is { Success: true } drive)
        {
            text = $"{char.ToUpperInvariant(drive.Groups[1].Value[0])}:\\{text[3..]}";
        }

        if (Path.IsPathFullyQualified(text))
        {
            return Exists(text, fileExists, null);
        }

        if (context.ProjectRoot is { } project)
        {
            return Exists(Path.GetFullPath(Path.Combine(project, text)), fileExists, "작업 폴더가 프로젝트 루트라고 보고 확인했습니다.");
        }

        return new HookScriptCheck(HookScriptStatus.Unknown, token, "사용자 설정의 상대 경로는 Claude Code를 실행한 폴더 기준입니다.");
    }

    private static HookScriptCheck Exists(string path, Func<string, bool> fileExists, string? note)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new HookScriptCheck(HookScriptStatus.Unknown, path, "경로 형식을 해석할 수 없습니다.");
        }

        return new HookScriptCheck(fileExists(normalized) ? HookScriptStatus.Exists : HookScriptStatus.Missing, normalized, note);
    }

    /// <summary>A file name or path with an extension other than .exe (variables count as path text).</summary>
    private static bool LooksLikeScript(string token)
    {
        var extension = Path.GetExtension(VariablePattern().Replace(token, "x"));
        return extension.Length > 1 && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    // cmd / PowerShell switches such as /c, /d, /s.
    private static bool IsSwitch(string token) => token.Length == 2 && token[0] == '/';

    internal static IEnumerable<string> Tokenize(string command)
    {
        var i = 0;
        while (i < command.Length)
        {
            while (i < command.Length && char.IsWhiteSpace(command[i]))
            {
                i++;
            }

            if (i >= command.Length)
            {
                yield break;
            }

            if (command[i] is '"' or '\'')
            {
                var quote = command[i];
                var end = command.IndexOf(quote, i + 1);
                end = end < 0 ? command.Length : end;
                yield return command[(i + 1)..end];
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < command.Length && !char.IsWhiteSpace(command[i]))
                {
                    i++;
                }

                yield return command[start..i];
            }
        }
    }

    // ${NAME}, $env:NAME, %NAME%, $NAME
    [GeneratedRegex(@"\$\{(?<a>[A-Za-z_][A-Za-z0-9_]*)(?::-[^}]*)?\}|\$env:(?<b>[A-Za-z_][A-Za-z0-9_]*)|%(?<c>[A-Za-z_][A-Za-z0-9_]*)%|\$(?<d>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex VariablePattern();

    [GeneratedRegex(@"^/([a-zA-Z])/")]
    private static partial Regex GitBashDrive();
}
