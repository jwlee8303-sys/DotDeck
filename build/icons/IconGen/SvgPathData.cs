using System.Globalization;
using System.Text;

namespace DotDeck.IconGen;

/// <summary>An SVG feature the converter does not support; the icon falls back to a generic one.</summary>
public sealed class SvgConversionException(string message) : Exception(message);

/// <summary>
/// Rewrites SVG path data as WPF path markup. SVG allows forms WPF does not parse (compact arc flags "011",
/// implicit commands after the first point, numbers like "1.5.5"), so every segment is re-emitted with an explicit
/// command and separators.
/// </summary>
internal static class SvgPathData
{
    public static string ToWpf(string data, bool evenOdd)
    {
        var reader = new Reader(data);
        var sb = new StringBuilder(evenOdd ? "F0" : "F1");
        char? command = null;
        var segments = 0;
        while (true)
        {
            reader.SkipSeparators();
            if (reader.End)
            {
                break;
            }

            char current;
            if (IsCommand(reader.Peek()))
            {
                current = reader.Next();
                command = current;
            }
            else if (command is null or 'Z' or 'z')
            {
                throw new SvgConversionException($"path data has a number where a command is expected at {reader.Position}");
            }
            else
            {
                // After a moveto, further coordinate pairs are implicit linetos.
                current = command.Value switch { 'M' => 'L', 'm' => 'l', var c => c };
                command = current;
            }

            if (segments == 0 && current is not ('M' or 'm'))
            {
                throw new SvgConversionException("path data must start with a moveto");
            }

            segments++;
            sb.Append(' ').Append(current);
            if (current is 'Z' or 'z')
            {
                continue;
            }

            if (current is 'A' or 'a')
            {
                Append(sb, reader.Number(), reader.Number(), reader.Number());
                sb.Append(' ').Append(reader.Flag()).Append(' ').Append(reader.Flag());
                Append(sb, reader.Number(), reader.Number());
                continue;
            }

            for (var i = ArgumentCount(current); i > 0; i--)
            {
                Append(sb, reader.Number());
            }
        }

        if (segments == 0)
        {
            throw new SvgConversionException("path data is empty");
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, params ReadOnlySpan<double> values)
    {
        foreach (var value in values)
        {
            sb.Append(' ').Append(Num.Format(value));
        }
    }

    private static bool IsCommand(char c) => "MmLlHhVvCcSsQqTtAaZz".Contains(c, StringComparison.Ordinal);

    private static int ArgumentCount(char command) => char.ToUpperInvariant(command) switch
    {
        'M' or 'L' or 'T' => 2,
        'H' or 'V' => 1,
        'C' => 6,
        'S' or 'Q' => 4,
        _ => throw new SvgConversionException($"unknown path command '{command}'"),
    };

    private sealed class Reader(string text)
    {
        private int _pos;

        public bool End => _pos >= text.Length;

        public int Position => _pos;

        public char Peek() => text[_pos];

        public char Next() => text[_pos++];

        public void SkipSeparators()
        {
            while (!End && (char.IsWhiteSpace(text[_pos]) || text[_pos] == ','))
            {
                _pos++;
            }
        }

        public char Flag()
        {
            SkipSeparators();
            if (End || text[_pos] is not ('0' or '1'))
            {
                throw new SvgConversionException($"arc flag expected at {_pos}");
            }

            return text[_pos++];
        }

        public double Number()
        {
            SkipSeparators();
            var start = _pos;
            if (!End && text[_pos] is '+' or '-')
            {
                _pos++;
            }

            var digits = SkipDigits();
            if (!End && text[_pos] == '.')
            {
                _pos++;
                digits += SkipDigits();
            }

            if (digits == 0)
            {
                throw new SvgConversionException($"number expected at {start}");
            }

            if (!End && text[_pos] is 'e' or 'E')
            {
                var mark = _pos++;
                if (!End && text[_pos] is '+' or '-')
                {
                    _pos++;
                }

                if (SkipDigits() == 0)
                {
                    _pos = mark;
                }
            }

            return double.Parse(text.AsSpan(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private int SkipDigits()
        {
            var start = _pos;
            while (!End && char.IsAsciiDigit(text[_pos]))
            {
                _pos++;
            }

            return _pos - start;
        }
    }
}

/// <summary>Invariant, round-trippable number text for the generated XAML.</summary>
internal static class Num
{
    public static string Format(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new SvgConversionException("non-finite number");
        }

        // Round away binary noise from transforms (e.g. 0.30000000000000004) so output stays small and stable.
        var rounded = Math.Round(value, 6);
        return rounded == 0 ? "0" : rounded.ToString(CultureInfo.InvariantCulture);
    }

    public static double Parse(string text, string what)
    {
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            throw new SvgConversionException($"invalid number for {what}: '{text}'");
        }

        return value;
    }
}
