using System.Runtime.Versioning;
using DotDeck.Core;

namespace DotDeck.Core.Tests;

public class ArchitectureTests
{
    // The primary guard: a platform-neutral TFM (net10.0) cannot compile against WPF
    // and cannot restore net10.0-windows-only packages (NU1202), including transitively.
    [Fact]
    public void Core_targets_platform_neutral_framework()
    {
        var platform = CoreAssembly.Instance.GetCustomAttributes(typeof(TargetPlatformAttribute), false);
        Assert.Empty(platform);

        var framework = (TargetFrameworkAttribute?)Attribute.GetCustomAttribute(
            CoreAssembly.Instance, typeof(TargetFrameworkAttribute));
        Assert.NotNull(framework);
        Assert.StartsWith(".NETCoreApp,", framework.FrameworkName, StringComparison.Ordinal);
    }

    // Secondary check on emitted references.
    private static readonly string[] s_forbiddenAssemblies =
    [
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "System.Windows.Forms",
        "System.Xaml",
    ];

    [Fact]
    public void Core_does_not_reference_ui_assemblies()
    {
        var referenced = CoreAssembly.Instance
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var forbidden in s_forbiddenAssemblies)
        {
            Assert.DoesNotContain(forbidden, referenced);
        }
    }
}
