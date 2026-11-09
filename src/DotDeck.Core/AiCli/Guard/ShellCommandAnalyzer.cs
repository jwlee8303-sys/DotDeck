using System.Text;

namespace DotDeck.Core.AiCli.Guard;

public enum ShellKind
{
    Bash,
    PowerShell,
}

/// <summary>What a shell command line seems to touch, as far as static inspection can tell.</summary>
public sealed record ShellAnalysis(IReadOnlyList<string> PathCandidates, IReadOnlyList<string> BareWords, string? AskReason);

/// <summary>
/// Best-effort static inspection of a Bash or PowerShell command (SPEC 11.7). It extracts tokens that look like paths
/// and redirection targets, and flags constructs whose effect cannot be determined (directory changes, variables,
/// sub-shells, nested shells, interpreters, script execution, downloads). It cannot prove a command is safe: shells run
/// arbitrary code, which is why non-interactive sessions exclude shell tools by default.
/// </summary>
public static class ShellCommandAnalyzer
{
    private static readonly HashSet<string> AskCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        // Directory changes make later relative paths unknowable.
        "cd", "chdir", "pushd", "popd", "set-location", "sl", "push-location", "pop-location",
        // Nested shells and evaluation.
        "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe", "bash", "bash.exe", "sh", "zsh", "wsl", "wsl.exe",
        "iex", "invoke-expression", "invoke-command", "icm", "eval", "exec", "source", ".", "&", "call",
        "start", "start-process", "saps", "runas", "schtasks", "at", "xargs", "env", "sudo", "find",
        // Interpreters run code that can write anywhere.
        "python", "python3", "python.exe", "py", "py.exe", "node", "node.exe", "deno", "bun", "ruby", "perl", "php", "lua",
        "dotnet-script", "csi", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "msbuild",
        // Downloads and copies with destinations in options.
        "curl", "curl.exe", "wget", "iwr", "invoke-webrequest", "irm", "invoke-restmethod", "certutil", "bitsadmin",
        "start-bitstransfer", "robocopy", "xcopy", "tar", "unzip", "expand-archive", "7z",
        // Registry and environment edits are outside the file scope model.
        "reg", "reg.exe", "setx", "set-itemproperty", "new-itemproperty", "remove-itemproperty",
        "[environment]::setenvironmentvariable",
    };

    private static readonly HashSet<string> AskOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "-encodedcommand", "-enc", "-e", "-ec", "-command", "-c", "-file", "-f",
    };

    private static readonly string[] ScriptExtensions = [".ps1", ".psm1", ".bat", ".cmd", ".sh", ".py", ".js", ".vbs", ".exe", ".com"];

    public static ShellAnalysis Analyze(string? command, ShellKind kind)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new([], [], null);
        }

        string? askReason = null;
        void Ask(string reason) => askReason ??= reason;

        if (HasExpansion(command, kind))
        {
            Ask("변수·서브셸 확장이 있어 대상 경로를 확정할 수 없습니다");
        }

        var tokens = Tokenize(command, kind);
        var paths = new List<string>();
        var bare = new List<string>();
        var commandStart = true;
        var redirectNext = false;
        foreach (var token in tokens)
        {
            if (token.IsOperator)
            {
                redirectNext = token.Text.Contains('>', StringComparison.Ordinal) || token.Text == "<";
                commandStart = !redirectNext;
                continue;
            }

            var text = token.Text;
            if (redirectNext)
            {
                redirectNext = false;
                if (!string.Equals(text, "&1", StringComparison.Ordinal) && !string.Equals(text, "&2", StringComparison.Ordinal))
                {
                    paths.Add(text);
                }

                continue;
            }

            if (commandStart)
            {
                commandStart = false;
                if (AskCommands.Contains(text) || text.StartsWith("[environment]::", StringComparison.OrdinalIgnoreCase))
                {
                    Ask($"'{text}' 명령은 대상 경로를 확정할 수 없습니다");
                }
                else if (LooksLikeScript(text))
                {
                    Ask($"스크립트·프로그램 실행({text})은 대상 경로를 확정할 수 없습니다");
                }

                if (LooksLikePath(text))
                {
                    paths.Add(text);
                }

                continue;
            }

            if (AskOptions.Contains(text))
            {
                Ask($"'{text}' 옵션은 대상 경로를 확정할 수 없습니다");
            }

            // --out=D:\x, -o:D:\x
            var value = text;
            if (value.StartsWith('-'))
            {
                var eq = value.IndexOfAny(['=', ':']);
                if (eq < 0)
                {
                    continue;
                }

                value = value[(eq + 1)..];
            }

            if (LooksLikePath(value))
            {
                paths.Add(value);
            }
            else if (value.Length > 0)
            {
                bare.Add(value);
            }
        }

        return new(paths, bare, askReason);
    }

    /// <summary>
    /// Converts a Git Bash style absolute path (<c>/c/Users/x</c>) to Windows form; returns null for other POSIX
    /// absolute paths (<c>/tmp</c>, <c>/usr</c>) whose mapping depends on the shell installation.
    /// </summary>
    public static string? FromPosix(string path)
    {
        if (path.Length >= 2 && path[0] == '/' && char.IsAsciiLetter(path[1]) && (path.Length == 2 || path[2] == '/'))
        {
            return char.ToUpperInvariant(path[1]) + ":\\" + (path.Length > 3 ? path[3..].Replace('/', '\\') : string.Empty);
        }

        return null;
    }

    /// <summary>The part of a wildcard path before the first segment containing <c>*</c>, <c>?</c> or <c>[</c>.</summary>
    public static string FixedPrefix(string path)
    {
        var index = path.IndexOfAny(['*', '?', '[']);
        if (index < 0)
        {
            return path;
        }

        var separator = path.LastIndexOfAny(['\\', '/'], index);
        return separator < 0 ? "." : path[..(separator + 1)];
    }

    private static bool LooksLikePath(string text)
    {
        if (text.Length == 0 || text.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        return text.Contains('\\', StringComparison.Ordinal)
            || text.Contains('/', StringComparison.Ordinal)
            || text.StartsWith('~')
            || text == ".."
            || (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':');
    }

    private static bool LooksLikeScript(string text)
    {
        var name = text.Replace('/', '\\');
        var looksRelative = name.StartsWith(@".\", StringComparison.Ordinal) || name.StartsWith(@"..\", StringComparison.Ordinal);
        return looksRelative || ScriptExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasExpansion(string command, ShellKind kind)
    {
        var inSingle = false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c == '\'')
            {
                inSingle = !inSingle;
                continue;
            }

            if (inSingle)
            {
                continue; // Single quotes are literal in both shells.
            }

            if (c == '$' && i + 1 < command.Length)
            {
                var n = command[i + 1];
                if (char.IsAsciiLetter(n) || n is '_' or '{' or '(' or ':')
                {
                    return true;
                }
            }

            if (kind == ShellKind.Bash && c == '`')
            {
                return true;
            }

            if (c == '%' && command.IndexOf('%', i + 1) > i + 1)
            {
                return true; // cmd-style %VAR%
            }
        }

        return false;
    }

    private readonly record struct Token(string Text, bool IsOperator);

    private static List<Token> Tokenize(string command, ShellKind kind)
    {
        var tokens = new List<Token>();
        var current = new StringBuilder();
        var hasCurrent = false;
        char quote = '\0';

        void Flush()
        {
            if (hasCurrent)
            {
                tokens.Add(new(current.ToString(), false));
                current.Clear();
                hasCurrent = false;
            }
        }

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (c == '\\' && quote == '"' && kind == ShellKind.Bash && i + 1 < command.Length && command[i + 1] is '"' or '\\' or '$' or '`')
                {
                    current.Append(command[++i]);
                }
                else if (c == '`' && quote == '"' && kind == ShellKind.PowerShell && i + 1 < command.Length)
                {
                    current.Append(command[++i]);
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    hasCurrent = true;
                    continue;
                case '\r' or '\n':
                    // An unquoted line break ends the command like ';' (the next word starts a new command).
                    Flush();
                    tokens.Add(new(";", true));
                    continue;
                case ' ' or '\t':
                    Flush();
                    continue;
                case ';' or '|' or '&' or '(' or ')' or '{' or '}' or '<' or '>':
                {
                    // "2>" / "*>" redirections: drop the stream number from the previous word.
                    if (c == '>' && hasCurrent && current.ToString() is "1" or "2" or "3" or "*")
                    {
                        current.Clear();
                        hasCurrent = false;
                    }

                    Flush();
                    var op = new StringBuilder().Append(c);
                    while (i + 1 < command.Length && command[i + 1] is '>' or '&' or '|')
                    {
                        // ">&1" style duplications stay with their target word below.
                        if (command[i + 1] == '&' && c == '>')
                        {
                            break;
                        }

                        op.Append(command[++i]);
                    }

                    tokens.Add(new(op.ToString(), true));
                    continue;
                }
                case '\\' when kind == ShellKind.Bash:
                    // Outside quotes Bash drops the backslash and keeps the next character literally (".en\v" is
                    // ".env", "C:\a" is "C:a"). A trailing backslash continues the line.
                    if (i + 1 < command.Length)
                    {
                        var next = command[++i];
                        if (next is not '\n')
                        {
                            current.Append(next);
                        }
                    }

                    hasCurrent = true;
                    continue;
                case '`' when kind == ShellKind.PowerShell && i + 1 < command.Length:
                    current.Append(command[++i]);
                    hasCurrent = true;
                    continue;
                default:
                    current.Append(c);
                    hasCurrent = true;
                    continue;
            }
        }

        Flush();
        return tokens;
    }
}
