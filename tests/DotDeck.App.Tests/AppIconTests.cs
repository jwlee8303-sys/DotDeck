using System.Buffers.Binary;
using System.IO;
using System.Windows;
using DotDeck.App.Infrastructure.Windows;

namespace DotDeck.App.Tests;

/// <summary>Generated icon files (build/icons/generate-icons.ps1) and the tray's ICO frame selection.</summary>
public sealed class AppIconTests
{
    [Theory]
    [InlineData("Tray/tray-on-light.ico")]
    [InlineData("Tray/tray-on-dark.ico")]
    public void Tray_icons_are_embedded_with_all_small_sizes(string name)
    {
        Assert.Equal([16, 20, 24, 32, 48], FrameSizes(ReadEmbedded(name)));
    }

    [Fact]
    public void App_icon_is_a_wpf_resource_with_every_shell_size()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack; // registers the pack:// scheme
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/DotDeck;component/Assets/Icons/DotDeck.ico"));
        Assert.NotNull(info);
        using var stream = info.Stream;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);

        Assert.Equal([16, 20, 24, 32, 40, 48, 64, 128, 256], FrameSizes(copy.ToArray()));
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(20, 20)]
    [InlineData(24, 24)]
    [InlineData(30, 32)] // between sizes: the next larger one, scaled down
    [InlineData(32, 32)]
    [InlineData(40, 48)]
    [InlineData(64, 48)] // larger than every frame: the largest
    public void Tray_frame_for_size(int size, int expected)
    {
        var ico = ReadEmbedded("Tray/tray-on-dark.ico");

        Assert.True(TrayIcon.TrySelectIcoFrame(ico, size, out var offset, out var length));

        // 32-bit DIB frames start with BITMAPINFOHEADER (40) and the width.
        Assert.True(length > 40);
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(offset)));
        Assert.Equal(expected, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(offset + 4)));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0, 0, 2, 0, 1, 0 })] // cursor, not icon
    [InlineData(new byte[] { 0, 0, 1, 0, 1, 0 })] // directory entry cut off
    public void Malformed_ico_is_rejected(byte[] ico)
    {
        Assert.False(TrayIcon.TrySelectIcoFrame(ico, 16, out _, out _));
    }

    [Fact]
    public void Frame_pointing_outside_the_file_is_skipped()
    {
        var ico = new byte[6 + 16];
        ico[2] = 1;
        ico[4] = 1;
        ico[6] = 16;
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(6 + 8), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(6 + 12), 22);

        Assert.False(TrayIcon.TrySelectIcoFrame(ico, 16, out _, out _));
    }

    private static byte[] ReadEmbedded(string name)
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static int[] FrameSizes(byte[] ico)
    {
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        return Enumerable.Range(0, count).Select(i => ico[6 + (16 * i)] is 0 ? 256 : ico[6 + (16 * i)]).Order().ToArray();
    }
}
