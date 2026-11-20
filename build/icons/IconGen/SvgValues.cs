using System.Globalization;
using System.Text.RegularExpressions;

namespace DotDeck.IconGen;

/// <summary>2D affine matrix in SVG order: x' = A·x + C·y + E, y' = B·x + D·y + F (same as WPF M11 M12 M21 M22 OffsetX OffsetY).</summary>
internal readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static Affine Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public bool IsIdentity => this == Identity;

    /// <summary>this ∘ other: applies <paramref name="other"/> first.</summary>
    public Affine Then(Affine other) => new(
        A * other.A + C * other.B,
        B * other.A + D * other.B,
        A * other.C + C * other.D,
        B * other.C + D * other.D,
        A * other.E + C * other.F + E,
        B * other.E + D * other.F + F);

    public static Affine Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    public static Affine Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    /// <summary>WPF transform text ("m11,m12,m21,m22,offsetX,offsetY").</summary>
    public string ToXaml() => string.Join(',', new[] { A, B, C, D, E, F }.Select(Num.Format));
}

internal static partial class SvgValues
{
    [GeneratedRegex(@"\s*([a-zA-Z]+)\s*\(([^)]*)\)\s*,?")]
    private static partial Regex TransformFunction();

    [GeneratedRegex(@"[\s,]+")]
    private static partial Regex ListSeparator();

    public static IReadOnlyList<double> Numbers(string text, string what) =>
        ListSeparator().Split(text.Trim()).Where(s => s.Length > 0).Select(s => Num.Parse(s, what)).ToList();

    public static Affine Transform(string text)
    {
        var result = Affine.Identity;
        var position = 0;
        foreach (Match m in TransformFunction().Matches(text))
        {
            if (m.Index != position)
            {
                throw new SvgConversionException($"invalid transform '{text}'");
            }

            position = m.Index + m.Length;
            var args = Numbers(m.Groups[2].Value, "transform");
            result = result.Then(Function(m.Groups[1].Value, args, text));
        }

        if (text[position..].Trim().Length > 0)
        {
            throw new SvgConversionException($"invalid transform '{text}'");
        }

        return result;
    }

    private static Affine Function(string name, IReadOnlyList<double> a, string text)
    {
        static double Rad(double degrees) => degrees * Math.PI / 180;
        return (name, a.Count) switch
        {
            ("matrix", 6) => new Affine(a[0], a[1], a[2], a[3], a[4], a[5]),
            ("translate", 1) => Affine.Translate(a[0], 0),
            ("translate", 2) => Affine.Translate(a[0], a[1]),
            ("scale", 1) => Affine.Scale(a[0], a[0]),
            ("scale", 2) => Affine.Scale(a[0], a[1]),
            ("rotate", 1) => Rotate(Rad(a[0])),
            ("rotate", 3) => Affine.Translate(a[1], a[2]).Then(Rotate(Rad(a[0]))).Then(Affine.Translate(-a[1], -a[2])),
            ("skewX", 1) => new Affine(1, 0, Math.Tan(Rad(a[0])), 1, 0, 0),
            ("skewY", 1) => new Affine(1, Math.Tan(Rad(a[0])), 0, 1, 0, 0),
            _ => throw new SvgConversionException($"unsupported transform '{name}' in '{text}'"),
        };
    }

    private static Affine Rotate(double r) => new(Math.Cos(r), Math.Sin(r), -Math.Sin(r), Math.Cos(r), 0, 0);

    /// <summary>A length or percentage; percentages are relative to <paramref name="reference"/>.</summary>
    public static double Length(string text, double reference, string what)
    {
        var t = text.Trim();
        if (t.EndsWith('%'))
        {
            return Num.Parse(t[..^1], what) / 100 * reference;
        }

        if (t.EndsWith("px", StringComparison.Ordinal))
        {
            t = t[..^2];
        }

        return Num.Parse(t, what);
    }

    public static double Opacity(string text, string what)
    {
        var t = text.Trim();
        var value = t.EndsWith('%') ? Num.Parse(t[..^1], what) / 100 : Num.Parse(t, what);
        return Math.Clamp(value, 0, 1);
    }

    /// <summary>Parses a CSS color to ARGB. Throws for anything else.</summary>
    public static (byte A, byte R, byte G, byte B) Color(string text)
    {
        var t = text.Trim();
        if (t.StartsWith('#'))
        {
            var hex = t[1..];
            if (hex.Length is 3 or 4)
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }

            if (hex.Length is 6 or 8 && uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
            {
                return hex.Length == 6
                    ? ((byte)255, (byte)(v >> 16), (byte)(v >> 8), (byte)v)
                    : ((byte)v, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8));
            }

            throw new SvgConversionException($"invalid color '{text}'");
        }

        if (t.StartsWith("rgb", StringComparison.OrdinalIgnoreCase) && t.EndsWith(')'))
        {
            var open = t.IndexOf('(', StringComparison.Ordinal);
            var parts = t[(open + 1)..^1].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is 3 or 4)
            {
                byte Channel(string p) => (byte)Math.Round(Math.Clamp(p.EndsWith('%') ? Num.Parse(p[..^1], "color") * 2.55 : Num.Parse(p, "color"), 0, 255));
                var alpha = parts.Length == 4 ? (byte)Math.Round(Opacity(parts[3], "color") * 255) : (byte)255;
                return (alpha, Channel(parts[0]), Channel(parts[1]), Channel(parts[2]));
            }

            throw new SvgConversionException($"invalid color '{text}'");
        }

        if (t.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            return (0, 0, 0, 0);
        }

        // CSS named colors match .NET's known colors, except the "grey" spellings.
        var known = System.Drawing.Color.FromName(t.Replace("grey", "gray", StringComparison.OrdinalIgnoreCase));
        if (known.IsKnownColor && !known.IsSystemColor)
        {
            return (known.A, known.R, known.G, known.B);
        }

        throw new SvgConversionException($"unsupported color '{text}'");
    }

    public static string ColorXaml((byte A, byte R, byte G, byte B) c, double opacity = 1) =>
        string.Create(CultureInfo.InvariantCulture, $"#{(byte)Math.Round(c.A * Math.Clamp(opacity, 0, 1)):X2}{c.R:X2}{c.G:X2}{c.B:X2}");
}
