using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Markup;
using System.Windows.Media;

namespace DotDeck.App.Tests;

/// <summary>The generated file icons embedded in DotDeck.exe (build/icons/update-file-icons.ps1).</summary>
public sealed class FileIconAssetsTests
{
    private const string IconPrefix = "FileIcons/icons/";
    private static readonly Assembly s_app = typeof(DotDeck.App.App).Assembly;

    private static IReadOnlyList<string> IconResources() =>
        [.. s_app.GetManifestResourceNames().Where(n => n.StartsWith(IconPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    [Fact]
    public void Every_manifest_icon_is_embedded()
    {
        using var stream = s_app.GetManifestResourceStream("FileIcons/material-icons.json");
        Assert.NotNull(stream);
        using var doc = JsonDocument.Parse(stream);
        var keys = doc.RootElement.GetProperty("iconDefinitions").EnumerateObject().Select(p => p.Name).ToList();
        var embedded = IconResources().Select(n => n[IconPrefix.Length..^".xaml".Length]).ToHashSet(StringComparer.Ordinal);

        Assert.True(keys.Count > 1000);
        Assert.DoesNotContain(keys, k => !embedded.Contains(k));
        Assert.Contains("file", embedded);
        Assert.Contains("folder-open", embedded);
    }

    [Fact]
    public void Every_embedded_icon_loads_as_a_frozen_16px_drawing()
    {
        var failures = new List<string>();
        var thread = new Thread(() =>
        {
            foreach (var name in IconResources())
            {
                try
                {
                    using var stream = s_app.GetManifestResourceStream(name)!;
                    var image = (DrawingImage)XamlReader.Load(stream);
                    image.Freeze();
                    if (Math.Abs(image.Width - 16) > 1e-6 || Math.Abs(image.Height - 16) > 1e-6)
                    {
                        failures.Add($"{name}: {image.Width}x{image.Height}");
                    }
                }
                catch (Exception ex) when (ex is XamlParseException or InvalidCastException or InvalidOperationException)
                {
                    failures.Add($"{name}: {ex.Message}");
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Empty(failures);
    }

    [Fact]
    public void Icon_license_ships_next_to_the_app()
    {
        var license = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "licenses", "LICENSE-material-icon-theme.txt"));
        Assert.Contains("MIT License", license, StringComparison.Ordinal);
        Assert.Contains("Permission is hereby granted, free of charge", license, StringComparison.Ordinal);
    }
}
