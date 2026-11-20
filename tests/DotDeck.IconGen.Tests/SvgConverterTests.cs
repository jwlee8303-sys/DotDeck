using System.Windows.Markup;
using System.Windows.Media;
using DotDeck.IconGen;

namespace DotDeck.IconGen.Tests;

public sealed class SvgPathDataTests
{
    [Theory]
    // Compact arc flags ("011") and implicit repeats are rewritten with explicit commands.
    [InlineData("M1 1a1 1 0 011 1", "F1 M 1 1 a 1 1 0 0 1 1 1")]
    [InlineData("m1 1 2 2 3 3", "F1 m 1 1 l 2 2 l 3 3")]
    [InlineData("M1.5.5L-2-3e1z", "F1 M 1.5 0.5 L -2 -30 z")]
    [InlineData("M0,0 H4 V4 Z M1 1 h1", "F1 M 0 0 H 4 V 4 Z M 1 1 h 1")]
    public void Path_data_is_rewritten_as_explicit_wpf_markup(string svg, string expected) =>
        Assert.Equal(expected, SvgPathData.ToWpf(svg, evenOdd: false));

    [Fact]
    public void Even_odd_uses_the_F0_prefix() => Assert.StartsWith("F0 ", SvgPathData.ToWpf("M0 0h1v1z", evenOdd: true), StringComparison.Ordinal);

    [Theory]
    [InlineData("L1 1")]
    [InlineData("M1 1 a1 1 0 2 1 1 1")]
    [InlineData("M1")]
    [InlineData("")]
    public void Invalid_path_data_is_rejected(string svg) =>
        Assert.Throws<SvgConversionException>(() => SvgPathData.ToWpf(svg, evenOdd: false));
}

public sealed class SvgValuesTests
{
    [Fact]
    public void Transform_list_applies_right_to_left()
    {
        // translate(10,0) scale(2): the point is scaled first, then moved.
        var m = SvgValues.Transform("translate(10 0) scale(2)");
        Assert.Equal(new Affine(2, 0, 0, 2, 10, 0), m);
    }

    [Fact]
    public void Rotate_about_a_point_keeps_that_point()
    {
        var m = SvgValues.Transform("rotate(90 8 8)");
        Assert.Equal(8, m.A * 8 + m.C * 8 + m.E, 9);
        Assert.Equal(8, m.B * 8 + m.D * 8 + m.F, 9);
    }

    [Theory]
    [InlineData("#abc", "#FFAABBCC")]
    [InlineData("#11223380", "#80112233")]
    [InlineData("rgb(255, 0, 0)", "#FFFF0000")]
    [InlineData("grey", "#FF808080")]
    [InlineData("transparent", "#00000000")]
    public void Colors_are_converted_to_argb(string css, string expected) => Assert.Equal(expected, SvgValues.ColorXaml(SvgValues.Color(css)));

    [Fact]
    public void Unknown_color_is_rejected() => Assert.Throws<SvgConversionException>(() => SvgValues.Color("menu"));
}

public sealed class SvgConverterTests
{
    private const string Ns = "xmlns=\"http://www.w3.org/2000/svg\"";

    private static string Svg(string body, string viewBox = "0 0 16 16") => $"<svg {Ns} viewBox=\"{viewBox}\">{body}</svg>";

    /// <summary>Loads like the app does and returns the frozen image (WPF types need an STA thread).</summary>
    private static DrawingImage Load(string xaml)
    {
        DrawingImage? image = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.Null(Program.ValidateWithWpf(xaml));
                image = (DrawingImage)XamlReader.Parse(xaml);
                image.Freeze();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return error is null ? image! : throw new InvalidOperationException("WPF load failed", error);
    }

    [Fact]
    public void Shapes_gradients_use_and_clip_convert_to_a_loadable_16px_image()
    {
        var xaml = SvgConverter.Convert(Svg("""
            <defs>
              <linearGradient id="g" x1="0" y1="0" x2="32" y2="0" gradientUnits="userSpaceOnUse" gradientTransform="rotate(45)">
                <stop offset="0" stop-color="#f00"/><stop offset="100%" style="stop-color:#00f;stop-opacity:.5"/>
              </linearGradient>
              <clipPath id="c"><rect width="16" height="16"/></clipPath>
              <circle id="dot" cx="2" cy="2" r="2"/>
            </defs>
            <g clip-path="url(#c)" opacity=".8" transform="translate(1 1)">
              <rect x="2" y="2" width="20" height="10" rx="2" fill="url(#g)"/>
              <path d="M2 2h4v4z" fill="none" stroke="#0f0" stroke-width="2" stroke-linejoin="round" style="font-family:x;isolation:isolate"/>
            </g>
            <use href="#dot" x="10" y="10" fill="#123456"/>
            <ellipse cx="8" cy="8" rx="1" ry="2" fill-opacity=".5"/>
            """, viewBox: "0 0 32 32"));

        Load(xaml);
        Assert.Contains("Transform=\"0.5,0,0,0.5,0,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MappingMode=\"Absolute\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<RectangleGeometry Rect=\"2,2,20,10\" RadiusX=\"2\" RadiusY=\"2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Color=\"#800000FF\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DrawingGroup.ClipGeometry", xaml, StringComparison.Ordinal);
        Assert.Contains("Center=\"2,2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Transform=\"1,0,0,1,10,10\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Brush=\"#80000000\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_bounds_are_the_viewbox_even_when_content_is_smaller()
    {
        var image = Load(SvgConverter.Convert(Svg("<rect x=\"4\" y=\"4\" width=\"2\" height=\"2\"/>", "0 0 24 24")));
        Assert.Equal(16, image.Width, 6);
        Assert.Equal(16, image.Height, 6);
    }

    [Fact]
    public void Non_square_viewbox_is_centered()
    {
        var xaml = SvgConverter.Convert(Svg("<rect width=\"32\" height=\"16\"/>", "0 0 32 16"));
        Assert.Contains("Transform=\"0.5,0,0,0.5,0,4\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Paint_order_stroke_draws_the_stroke_below_the_fill()
    {
        var xaml = SvgConverter.Convert(Svg("<path d=\"M1 1h4v4z\" fill=\"#f00\" stroke=\"#000\" paint-order=\"stroke\"/>"));
        Assert.True(xaml.IndexOf("<Pen", StringComparison.Ordinal) < xaml.IndexOf("Brush=\"#FFFF0000\"", StringComparison.Ordinal));
        Load(xaml);
    }

    [Fact]
    public void Recolor_replaces_colors_but_keeps_alpha()
    {
        var xaml = SvgConverter.Convert(Svg("<path d=\"M1 1h4v4z\" fill=\"#f00\" fill-opacity=\".5\"/>"), new SvgConvertOptions("#00ff00"));
        Assert.Contains("Brush=\"#8000FF00\"", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<text>x</text>", "unsupported element <text>")]
    [InlineData("<path d=\"M0 0h1\" filter=\"url(#f)\"/>", "unsupported attribute 'filter'")]
    [InlineData("<path d=\"M0 0h1\" style=\"mix-blend-mode:multiply\"/>", "unsupported style")]
    [InlineData("<svg viewBox=\"0 0 1 1\"/>", "nested <svg>")]
    [InlineData("<use href=\"#missing\"/>", "not found")]
    [InlineData("<path d=\"M0 0h1\" fill=\"url(http://x/y#g)\"/>", "unsupported url")]
    public void Unsupported_features_fail_instead_of_rendering_wrongly(string body, string message)
    {
        var ex = Assert.Throws<SvgConversionException>(() => SvgConverter.Convert(Svg(body)));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Alpha of the pixel at (x, y) when the icon is drawn at 16×16 px.</summary>
    private static byte[] Alpha(string xaml, params (int X, int Y)[] points)
    {
        byte[]? result = null;
        var thread = new Thread(() =>
        {
            var image = (DrawingImage)XamlReader.Parse(xaml);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(image, new System.Windows.Rect(0, 0, 16, 16));
            }

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[16 * 16 * 4];
            bitmap.CopyPixels(pixels, 16 * 4, 0);
            result = [.. points.Select(p => pixels[(p.Y * 16 + p.X) * 4 + 3])];
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result!;
    }

    [Fact]
    public void Use_position_moves_its_clip_path_too()
    {
        var xaml = SvgConverter.Convert(Svg("""
            <defs><clipPath id="c"><rect width="4" height="4"/></clipPath><rect id="r" width="16" height="16"/></defs>
            <use href="#r" x="8" y="8" clip-path="url(#c)"/>
            """));
        Assert.Equal([255, 0, 0], Alpha(xaml, (10, 10), (2, 2), (14, 14)));
    }

    [Fact]
    public void Clip_children_are_united_even_when_their_directions_cancel()
    {
        var xaml = SvgConverter.Convert(Svg("""
            <defs><clipPath id="c"><path d="M0 0h8v8h-8z"/><path d="M4 4v8h8v-8z"/></clipPath></defs>
            <rect width="16" height="16" clip-path="url(#c)"/>
            """));
        Assert.Equal([255, 255, 255, 0], Alpha(xaml, (5, 5), (1, 1), (10, 10), (14, 1)));
    }

    [Fact]
    public void Visibility_is_inherited_and_can_be_turned_back_on()
    {
        var xaml = SvgConverter.Convert(Svg("""
            <g visibility="hidden"><rect width="4" height="4"/><rect x="8" width="4" height="4" visibility="visible"/></g>
            """));
        Assert.Equal([0, 255], Alpha(xaml, (2, 2), (10, 2)));
    }

    [Fact]
    public void Inline_style_wins_over_a_later_attribute() =>
        Assert.Contains("Brush=\"#FF0000FF\"", SvgConverter.Convert(Svg("<rect width=\"4\" height=\"4\" style=\"fill:#00f\" fill=\"#f00\"/>")), StringComparison.Ordinal);

    [Theory]
    [InlineData("<path d=\"M0 0h1\" xmlns:x=\"urn:x\" x:fill=\"red\"/>")]
    [InlineData("<defs><linearGradient id=\"a\" href=\"#b\"/><linearGradient id=\"b\" href=\"#a\"/></defs><rect width=\"4\" height=\"4\" fill=\"url(#a)\"/>")]
    [InlineData("<defs><linearGradient id=\"a\"><stop/><animate/></linearGradient></defs><rect width=\"4\" height=\"4\" fill=\"url(#a)\"/>")]
    [InlineData("<path d=\"M0 0h1\" stroke=\"#000\" stroke-linejoin=\"miter-clip\"/>")]
    [InlineData("<foo:bar xmlns:foo=\"urn:foo\"/>")]
    public void More_unsupported_features_fail(string body) =>
        Assert.Throws<SvgConversionException>(() => SvgConverter.Convert(Svg(body)));

    [Fact]
    public void Stylesheet_processing_instruction_is_rejected() =>
        Assert.Throws<SvgConversionException>(() => SvgConverter.Convert($"<?xml-stylesheet href=\"a.css\"?>{Svg("<rect width=\"4\" height=\"4\"/>")}"));

    [Fact]
    public void Editor_metadata_is_ignored() =>
        Load(SvgConverter.Convert(Svg("""<sodipodi:namedview xmlns:sodipodi="http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd"/><rect width="4" height="4" xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape" inkscape:label="x"/>""")));

    [Fact]
    public void Dtd_is_rejected() =>
        Assert.Throws<SvgConversionException>(() => SvgConverter.Convert($"<!DOCTYPE svg [<!ENTITY x \"y\">]><svg {Ns} viewBox=\"0 0 16 16\"/>"));
}
