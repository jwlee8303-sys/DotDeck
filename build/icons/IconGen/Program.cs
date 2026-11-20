using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace DotDeck.IconGen;

/// <summary>
/// File icon generator (run by build/icons/update-file-icons.ps1).
/// <code>
/// DotDeck.IconGen generate --icons &lt;dir&gt; --manifest &lt;material-icons.json&gt; --out &lt;dir&gt;
///                          [--exclude &lt;file-icon-exclude.json&gt;] [--extra &lt;dotdeck svg dir&gt;] [--report &lt;file.md&gt;]
/// </code>
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length == 0 || args[0] != "generate")
            {
                Console.Error.WriteLine("usage: DotDeck.IconGen generate --icons <dir> --manifest <file> --out <dir> [--exclude <file>] [--extra <dir>] [--report <file>]");
                return 2;
            }

            var named = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                {
                    throw new ArgumentException($"invalid argument '{args[i]}'");
                }

                named[args[i][2..]] = args[i + 1];
            }

            string Required(string name) => named.TryGetValue(name, out var v) ? v : throw new ArgumentException($"--{name} is required");
            var options = new GenerateOptions(
                Required("icons"), Required("manifest"), named.GetValueOrDefault("exclude"), named.GetValueOrDefault("extra"), Required("out"));
            var result = IconGenerator.Generate(options, ValidateWithWpfOnSta);

            Console.WriteLine($"converted {result.Converted}, failed {result.Failed.Count}, excluded {result.Excluded.Count}, DotDeck {result.Extra.Count}, removed {result.Removed}");
            foreach (var f in result.Failed)
            {
                Console.WriteLine($"  failed  {f.Key}: {f.Reason}");
            }

            foreach (var e in result.Excluded)
            {
                Console.WriteLine($"  excluded {e.Key} → {e.ReplaceWith ?? "generic"}");
            }

            if (named.TryGetValue("report", out var report))
            {
                File.WriteAllText(report, IconGenerator.Report(result), new UTF8Encoding(false));
            }

            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or SvgConversionException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary><see cref="ValidateWithWpf"/> on an STA thread (WPF objects need one), from any thread.</summary>
    internal static string? ValidateWithWpfOnSta(string xaml)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return ValidateWithWpf(xaml);
        }

        string? result = null;
        var thread = new Thread(() => result = ValidateWithWpf(xaml));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    /// <summary>Loads the XAML as the app will (DrawingImage, frozen) and checks its size is the 16×16 box.</summary>
    internal static string? ValidateWithWpf(string xaml)
    {
        try
        {
            if (XamlReader.Parse(xaml) is not DrawingImage image)
            {
                return "XAML is not a DrawingImage";
            }

            if (!image.CanFreeze)
            {
                return "DrawingImage cannot be frozen";
            }

            image.Freeze();
            var b = image.Drawing.Bounds;
            return Math.Abs(b.X) > 1e-6 || Math.Abs(b.Y) > 1e-6 || Math.Abs(b.Width - SvgConverter.Size) > 1e-6 || Math.Abs(b.Height - SvgConverter.Size) > 1e-6
                ? $"unexpected bounds {b}"
                : null;
        }
        catch (Exception ex) when (ex is XamlParseException or FormatException or InvalidOperationException)
        {
            return $"WPF could not load the XAML: {ex.Message}";
        }
    }
}
