using System.Text;

namespace DotDeck.Core.AiCli.Launch;

/// <summary>
/// Builds a <c>CreateProcess</c> command line that <c>CommandLineToArgvW</c> / the MSVC runtime split back into the
/// exact argument list (the same rules .NET uses for <c>ProcessStartInfo.ArgumentList</c>). ConPTY needs the command
/// line as one string, so this replaces <c>ArgumentList</c> for every AI CLI launch.
/// </summary>
public static class WindowsCommandLine
{
    /// <summary>Quotes one argument when needed.</summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        var sb = new StringBuilder(argument.Length + 2);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // Backslashes before a quote are doubled and the quote itself is escaped.
                sb.Append('\\', (backslashes * 2) + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(c);
            }

            backslashes = 0;
        }

        // Backslashes before the closing quote are doubled so they do not escape it.
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// The full command line: the executable (always quoted, it is parsed with different rules) followed by the
    /// quoted arguments.
    /// </summary>
    public static string Join(string executable, IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (executable.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("실행 파일 경로에 따옴표가 있습니다", nameof(executable));
        }

        var sb = new StringBuilder();
        sb.Append('"').Append(executable).Append('"');
        foreach (var argument in arguments)
        {
            sb.Append(' ').Append(Quote(argument));
        }

        return sb.ToString();
    }
}

/// <summary>TOML 1.0 basic string literal for Codex <c>-c key=value</c> overrides.</summary>
public static class TomlString
{
    public static string Basic(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
