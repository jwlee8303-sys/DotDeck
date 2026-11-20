using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DotDeck.IconGen;

/// <summary>Conversion options.</summary>
/// <param name="Recolor">When set, every solid or gradient color is replaced by this color (keeping alpha). Used for generic stand-ins.</param>
internal sealed record SvgConvertOptions(string? Recolor = null)
{
    public static SvgConvertOptions Default { get; } = new();
}

/// <summary>
/// Converts the SVG subset used by Material Icon Theme to a WPF <c>DrawingImage</c> in XAML. The drawing is scaled
/// to a 16×16 box (the viewBox, also kept as transparent bounds so Image scaling matches the SVG) and clipped to it.
/// Anything outside the supported subset throws <see cref="SvgConversionException"/>; nothing is silently dropped
/// except metadata and rendering hints.
/// </summary>
internal static class SvgConverter
{
    public const double Size = 16;

    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>Editor metadata (attributes and elements) with no rendering effect.</summary>
    private static readonly HashSet<string> s_metadataNamespaces =
    [
        "http://www.inkscape.org/namespaces/inkscape",
        "http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd",
        "http://www.w3.org/1999/02/22-rdf-syntax-ns#",
        "http://purl.org/dc/elements/1.1/",
        "http://creativecommons.org/ns#",
    ];

    /// <summary>Presentation properties, accepted as attributes or in <c>style</c>.</summary>
    private static readonly HashSet<string> s_presentation =
    [
        "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-opacity", "stroke-linejoin", "stroke-linecap",
        "stroke-miterlimit", "clip-rule", "color", "paint-order", "opacity", "clip-path", "display", "visibility",
        "stop-color", "stop-opacity",
    ];

    /// <summary>Attributes with no rendering effect here.</summary>
    private static readonly HashSet<string> s_ignorable =
    [
        "id", "class", "version", "role", "focusable", "image-rendering", "shape-rendering", "text-rendering",
        "color-rendering", "color-interpolation", "color-interpolation-filters", "enable-background", "overflow",
    ];

    private static readonly Dictionary<string, HashSet<string>> s_attributes = new()
    {
        ["svg"] = ["viewBox", "width", "height", "x", "y", "preserveAspectRatio", "transform"],
        ["g"] = ["transform"],
        ["path"] = ["d", "transform"],
        ["rect"] = ["x", "y", "width", "height", "rx", "ry", "transform"],
        ["circle"] = ["cx", "cy", "r", "transform"],
        ["ellipse"] = ["cx", "cy", "rx", "ry", "transform"],
        ["line"] = ["x1", "y1", "x2", "y2", "transform"],
        ["polygon"] = ["points", "transform"],
        ["polyline"] = ["points", "transform"],
        ["use"] = ["href", "x", "y", "width", "height", "transform"],
        ["defs"] = [],
        ["clipPath"] = ["clipPathUnits", "transform"],
        ["linearGradient"] = ["x1", "y1", "x2", "y2", "gradientUnits", "gradientTransform", "spreadMethod", "href"],
        ["radialGradient"] = ["cx", "cy", "r", "fx", "fy", "gradientUnits", "gradientTransform", "spreadMethod", "href"],
        ["stop"] = ["offset"],
        ["title"] = [],
        ["desc"] = [],
        ["metadata"] = [],
    };

    public static string Convert(string svgText, SvgConvertOptions? options = null)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(svgText), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new SvgConversionException($"invalid XML: {ex.Message}");
        }

        var root = doc.Root ?? throw new SvgConversionException("empty document");
        if (root.Name != Svg + "svg")
        {
            throw new SvgConversionException("root element is not <svg>");
        }

        if (doc.DescendantNodes().OfType<XProcessingInstruction>().Any())
        {
            throw new SvgConversionException("processing instructions (e.g. xml-stylesheet) are not supported");
        }

        var context = new Context(doc, options ?? SvgConvertOptions.Default);
        var (viewBox, fit) = ViewBox(root);
        context.ViewportWidth = viewBox.Width;
        context.ViewportHeight = viewBox.Height;

        var content = new List<XElement>();
        var rootProps = context.Props(root);
        if (rootProps.GetValueOrDefault("display")?.Trim() != "none")
        {
            context.RenderChildren(root, Paint.Initial.Inherit(rootProps, context.Diagonal), content);
        }

        var rootTransform = root.Attribute("transform") is { } rt ? SvgValues.Transform(rt.Value) : Affine.Identity;
        var inner = context.Wrap(root, content, transform: fit.Then(rootTransform), allowClip: true);

        var box = $"M0,0 H{Num.Format(Size)} V{Num.Format(Size)} H0 Z";
        var group = new XElement(W + "DrawingGroup", new XAttribute("ClipGeometry", box),
            new XElement(W + "GeometryDrawing", new XAttribute("Brush", "#00FFFFFF"), new XAttribute("Geometry", box)),
            inner);
        var image = new XElement(W + "DrawingImage", new XAttribute("xmlns", W.NamespaceName),
            new XElement(W + "DrawingImage.Drawing", group));
        return Serialize(image);
    }

    private static string Serialize(XElement element)
    {
        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
        }))
        {
            element.Save(writer);
        }

        return sb.Append('\n').ToString();
    }

    private readonly record struct Box(double X, double Y, double Width, double Height);

    private static (Box ViewBox, Affine Fit) ViewBox(XElement root)
    {
        Box box;
        if (root.Attribute("viewBox") is { } vb)
        {
            var n = SvgValues.Numbers(vb.Value, "viewBox");
            if (n.Count != 4 || n[2] <= 0 || n[3] <= 0)
            {
                throw new SvgConversionException($"invalid viewBox '{vb.Value}'");
            }

            box = new Box(n[0], n[1], n[2], n[3]);
        }
        else if (root.Attribute("width") is { } w && root.Attribute("height") is { } h)
        {
            box = new Box(0, 0, SvgValues.Length(w.Value, Size, "width"), SvgValues.Length(h.Value, Size, "height"));
            if (box.Width <= 0 || box.Height <= 0)
            {
                throw new SvgConversionException("invalid width/height");
            }
        }
        else
        {
            throw new SvgConversionException("svg has neither viewBox nor width/height");
        }

        var aspect = (root.Attribute("preserveAspectRatio")?.Value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (aspect.Length == 0)
        {
            aspect = ["xMidYMid"];
        }
        else if (aspect.Length > 2)
        {
            throw new SvgConversionException($"unsupported preserveAspectRatio '{string.Join(' ', aspect)}'");
        }

        var sx = Size / box.Width;
        var sy = Size / box.Height;
        double tx = 0, ty = 0;
        if (aspect[0] != "none")
        {
            if (aspect.Length > 1 && aspect[1] != "meet")
            {
                throw new SvgConversionException($"unsupported preserveAspectRatio '{string.Join(' ', aspect)}'");
            }

            var align = aspect[0];
            if (align.Length != 8 || !align.StartsWith('x') || align[4] != 'Y')
            {
                throw new SvgConversionException($"unsupported preserveAspectRatio '{align}'");
            }

            var s = Math.Min(sx, sy);
            double Offset(string part, double free) => part switch
            {
                "Min" => 0,
                "Mid" => free / 2,
                "Max" => free,
                _ => throw new SvgConversionException($"unsupported preserveAspectRatio '{align}'"),
            };
            tx = Offset(align[1..4], Size - box.Width * s);
            ty = Offset(align[5..8], Size - box.Height * s);
            sx = sy = s;
        }

        return (box, new Affine(sx, 0, 0, sy, tx - box.X * sx, ty - box.Y * sy));
    }

    /// <summary>Inherited painting state.</summary>
    private sealed record Paint
    {
        public static Paint Initial { get; } = new();

        public string Fill { get; init; } = "black";
        public double FillOpacity { get; init; } = 1;
        public bool FillEvenOdd { get; init; }
        public string Stroke { get; init; } = "none";
        public double StrokeWidth { get; init; } = 1;
        public double StrokeOpacity { get; init; } = 1;
        public string LineJoin { get; init; } = "miter";
        public string LineCap { get; init; } = "butt";
        public double MiterLimit { get; init; } = 4;
        public bool ClipEvenOdd { get; init; }
        public string Color { get; init; } = "black";
        public bool StrokeFirst { get; init; }

        /// <summary>visibility: hidden/collapse. Inherited, and a child may set it back to visible.</summary>
        public bool Hidden { get; init; }

        /// <param name="diagonal">Reference length for percentages that are not per-axis (normalized viewport diagonal).</param>
        public Paint Inherit(IReadOnlyDictionary<string, string> p, double diagonal)
        {
            var next = this;
            foreach (var (key, raw) in p)
            {
                var value = raw.Trim();
                if (value == "inherit")
                {
                    continue;
                }

                next = key switch
                {
                    "fill" => next with { Fill = value },
                    "fill-opacity" => next with { FillOpacity = SvgValues.Opacity(value, key) },
                    "fill-rule" => next with { FillEvenOdd = Rule(value, key) },
                    "stroke" => next with { Stroke = value },
                    "stroke-width" => next with { StrokeWidth = SvgValues.Length(value, diagonal, key) },
                    "stroke-opacity" => next with { StrokeOpacity = SvgValues.Opacity(value, key) },
                    "stroke-linejoin" => next with { LineJoin = value is "miter" or "round" or "bevel" ? value : throw Unsupported(key, value) },
                    "stroke-linecap" => next with { LineCap = value is "butt" or "round" or "square" ? value : throw Unsupported(key, value) },
                    "stroke-miterlimit" => next with { MiterLimit = Num.Parse(value, key) },
                    "clip-rule" => next with { ClipEvenOdd = Rule(value, key) },
                    "color" => next with { Color = value },
                    "visibility" => next with { Hidden = value is "hidden" or "collapse" || (value != "visible" ? throw Unsupported(key, value) : false) },
                    "paint-order" => next with { StrokeFirst = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => t != "markers") == "stroke" },
                    _ => next,
                };
            }

            return next;
        }

        private static bool Rule(string value, string key) => value switch
        {
            "nonzero" => false,
            "evenodd" => true,
            _ => throw Unsupported(key, value),
        };
    }

    /// <summary>
    /// Style properties with no effect on these shapes: text and CSS shape properties (no text or CSS layout is supported), editor extensions (Inkscape solid-color), and isolation (only
    /// meaningful with blend modes, which are themselves unsupported).
    /// </summary>
    private static bool IsIgnorableStyle(string key, string value) =>
        key.StartsWith("font-", StringComparison.Ordinal) || key.StartsWith("text-", StringComparison.Ordinal)
        || key.StartsWith("-inkscape-", StringComparison.Ordinal) || key.StartsWith("shape-", StringComparison.Ordinal)
        || key is "solid-color" or "solid-opacity"
        || (key == "mix-blend-mode" && value == "normal")
        || key is "isolation" or "inline-size" or "line-height" or "letter-spacing" or "word-spacing" or "white-space" or "writing-mode" or "direction";

    private static SvgConversionException Unsupported(string what, string value) => new($"unsupported {what} '{value}'");

    private sealed class Context(XDocument doc, SvgConvertOptions options)
    {
        private readonly Dictionary<string, XElement> _ids = doc.Descendants()
            .Where(e => e.Attribute("id") is not null)
            .GroupBy(e => e.Attribute("id")!.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        private int _depth;

        public double ViewportWidth { get; set; } = Size;

        public double ViewportHeight { get; set; } = Size;

        /// <summary>SVG's reference for non-axis percentages: sqrt((w² + h²) / 2).</summary>
        public double Diagonal => Math.Sqrt((ViewportWidth * ViewportWidth + ViewportHeight * ViewportHeight) / 2);

        private int _nesting;

        /// <summary>Presentation properties of an element (style overrides attributes), after checking every attribute is known.</summary>
        public Dictionary<string, string> Props(XElement e)
        {
            var name = e.Name.LocalName;
            if (!s_attributes.TryGetValue(name, out var own))
            {
                throw new SvgConversionException($"unsupported element <{name}>");
            }

            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            var styled = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var a in e.Attributes())
            {
                if (a.IsNamespaceDeclaration)
                {
                    continue;
                }

                var local = a.Name.LocalName;
                if (a.Name.Namespace == XLink && local == "href")
                {
                    continue;
                }

                if (a.Name.Namespace != XNamespace.None)
                {
                    // xml:space and editor metadata (inkscape:, sodipodi:) have no rendering effect.
                    if (a.Name.Namespace == XNamespace.Xml || s_metadataNamespaces.Contains(a.Name.NamespaceName))
                    {
                        continue;
                    }

                    throw new SvgConversionException($"unsupported attribute '{a.Name}' on <{name}>");
                }

                if (s_presentation.Contains(local))
                {
                    props[local] = a.Value;
                }
                else if (local == "style")
                {
                    foreach (var declaration in a.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var colon = declaration.IndexOf(':', StringComparison.Ordinal);
                        var key = colon < 0 ? declaration : declaration[..colon].Trim();
                        if (colon >= 0 && IsIgnorableStyle(key, declaration[(colon + 1)..].Trim()))
                        {
                            continue;
                        }

                        if (colon < 0 || !s_presentation.Contains(key))
                        {
                            throw new SvgConversionException($"unsupported style '{declaration}' on <{name}>");
                        }

                        styled[key] = declaration[(colon + 1)..].Trim();
                    }
                }
                else if (!own.Contains(local) && !s_ignorable.Contains(local)
                    && !local.StartsWith("data-", StringComparison.Ordinal) && !local.StartsWith("aria-", StringComparison.Ordinal))
                {
                    throw new SvgConversionException($"unsupported attribute '{local}' on <{name}>");
                }
            }

            // Inline style wins over presentation attributes, whatever their order.
            foreach (var (key, value) in styled)
            {
                props[key] = value;
            }

            return props;
        }

        public void RenderChildren(XElement parent, Paint paint, List<XElement> output)
        {
            foreach (var child in parent.Elements())
            {
                if (child.Name.Namespace != Svg)
                {
                    if (s_metadataNamespaces.Contains(child.Name.NamespaceName))
                    {
                        continue; // editor metadata elements
                    }

                    throw new SvgConversionException($"unsupported element <{child.Name}>");
                }

                Render(child, paint, output, extra: Affine.Identity);
            }
        }

        private void Render(XElement e, Paint inherited, List<XElement> output, Affine extra)
        {
            var name = e.Name.LocalName;
            var props = Props(e);
            if (name is "defs" or "clipPath" or "linearGradient" or "radialGradient" or "title" or "desc" or "metadata" or "stop")
            {
                return; // referenced elsewhere or not rendered
            }

            if (props.GetValueOrDefault("display")?.Trim() == "none")
            {
                return;
            }

            if (++_nesting > 64)
            {
                throw new SvgConversionException("elements nested too deeply");
            }

            var paint = inherited.Inherit(props, Diagonal);
            var content = new List<XElement>();
            switch (name)
            {
                case "g":
                    RenderChildren(e, paint, content);
                    break;
                case "svg":
                    throw new SvgConversionException("nested <svg>");
                case "use":
                    RenderUse(e, paint, content);
                    break;
                default:
                    if (!paint.Hidden && Geometry(e, paint.FillEvenOdd, Affine.Identity) is { } geometry)
                    {
                        Draw(geometry, paint, content);
                    }

                    break;
            }

            if (content.Count > 0)
            {
                var transform = e.Attribute("transform") is { } t ? SvgValues.Transform(t.Value) : Affine.Identity;
                if (name == "use")
                {
                    // x/y are an extra translation after the use element's own transform; its clip-path moves with it.
                    transform = transform.Then(Affine.Translate(Coordinate(e, "x", ViewportWidth), Coordinate(e, "y", ViewportHeight)));
                }

                output.AddRange(Wrap(e, content, extra.Then(transform), allowClip: true, props));
            }

            _nesting--;
        }

        private void RenderUse(XElement use, Paint paint, List<XElement> output)
        {
            var target = Reference(Href(use) ?? throw new SvgConversionException("<use> without href"));
            if (target.AncestorsAndSelf().Contains(use) || ++_depth > 16)
            {
                throw new SvgConversionException("recursive <use>");
            }

            Render(target, paint, output, Affine.Identity);
            _depth--;
        }

        /// <summary>Wraps rendered content in groups for transform, opacity and clip-path (clip and opacity apply in the transformed space).</summary>
        public IEnumerable<XElement> Wrap(XElement e, List<XElement> content, Affine transform, bool allowClip, IReadOnlyDictionary<string, string>? props = null)
        {
            props ??= Props(e);
            var opacity = props.TryGetValue("opacity", out var o) ? SvgValues.Opacity(o, "opacity") : 1;
            var clip = allowClip && props.TryGetValue("clip-path", out var c) && c.Trim() != "none" ? ClipGeometry(c) : null;
            if (opacity <= 0)
            {
                return [];
            }

            IEnumerable<XElement> result = content;
            if (clip is not null || opacity < 1)
            {
                var group = new XElement(W + "DrawingGroup");
                if (opacity < 1)
                {
                    group.Add(new XAttribute("Opacity", Num.Format(opacity)));
                }

                if (clip is not null)
                {
                    group.Add(new XElement(W + "DrawingGroup.ClipGeometry", clip));
                }

                group.Add(content);
                result = [group];
            }

            if (!transform.IsIdentity)
            {
                result = [new XElement(W + "DrawingGroup", new XAttribute("Transform", transform.ToXaml()), result)];
            }

            return result;
        }

        private XElement ClipGeometry(string value)
        {
            var target = Reference(Url(value) ?? throw Unsupported("clip-path", value));
            if (target.Name != Svg + "clipPath")
            {
                throw Unsupported("clip-path target", target.Name.LocalName);
            }

            var props = Props(target);
            if ((target.Attribute("clipPathUnits")?.Value ?? "userSpaceOnUse") != "userSpaceOnUse")
            {
                throw Unsupported("clipPathUnits", target.Attribute("clipPathUnits")!.Value);
            }

            var own = target.Attribute("transform") is { } t ? SvgValues.Transform(t.Value) : Affine.Identity;
            var paint = Paint.Initial.Inherit(props, Diagonal);
            var geometries = new List<XElement>();
            foreach (var child in target.Elements().Where(x => x.Name.Namespace == Svg))
            {
                CollectClip(child, paint, own, geometries);
            }

            if (geometries.Count == 0)
            {
                return new XElement(W + "RectangleGeometry", new XAttribute("Rect", "0,0,0,0"));
            }

            // The clip region is the union of the children, each filled with its own clip-rule.
            var union = geometries[0];
            foreach (var next in geometries.Skip(1))
            {
                union = new XElement(W + "CombinedGeometry", new XAttribute("GeometryCombineMode", "Union"),
                    new XElement(W + "CombinedGeometry.Geometry1", union),
                    new XElement(W + "CombinedGeometry.Geometry2", next));
            }

            return union;
        }

        private void CollectClip(XElement e, Paint inherited, Affine transform, List<XElement> output)
        {
            var name = e.Name.LocalName;
            var props = Props(e);
            if (name is "title" or "desc")
            {
                return;
            }

            if (props.ContainsKey("clip-path") && props["clip-path"].Trim() != "none")
            {
                throw new SvgConversionException("nested clip-path inside <clipPath>");
            }

            if (props.GetValueOrDefault("display")?.Trim() == "none")
            {
                return;
            }

            var paint = inherited.Inherit(props, Diagonal);
            var own = e.Attribute("transform") is { } t ? transform.Then(SvgValues.Transform(t.Value)) : transform;
            if (name == "use")
            {
                var target = Reference(Href(e) ?? throw new SvgConversionException("<use> without href"));
                if (++_depth > 16)
                {
                    throw new SvgConversionException("recursive <use>");
                }

                CollectClip(target, paint, own.Then(Affine.Translate(Coordinate(e, "x", ViewportWidth), Coordinate(e, "y", ViewportHeight))), output);
                _depth--;
                return;
            }

            if (name is "g")
            {
                throw new SvgConversionException("<g> inside <clipPath>");
            }

            if (!paint.Hidden && Geometry(e, paint.ClipEvenOdd, own) is { } geometry)
            {
                output.Add(geometry);
            }
        }

        private void Draw(XElement geometry, Paint paint, List<XElement> output)
        {
            var fill = Brush(paint.Fill, paint.FillOpacity, paint.Color);
            var stroke = paint.StrokeWidth > 0 ? Brush(paint.Stroke, paint.StrokeOpacity, paint.Color) : null;
            if (fill is null && stroke is null)
            {
                return;
            }

            XElement Drawing(object? brush, object? pen)
            {
                var d = new XElement(W + "GeometryDrawing");
                if (brush is string s)
                {
                    d.Add(new XAttribute("Brush", s));
                }
                else if (brush is XElement b)
                {
                    d.Add(new XElement(W + "GeometryDrawing.Brush", b));
                }

                d.Add(new XElement(W + "GeometryDrawing.Geometry", new XElement(geometry)));
                if (pen is XElement p)
                {
                    d.Add(new XElement(W + "GeometryDrawing.Pen", p));
                }

                return d;
            }

            var penElement = stroke is null ? null : Pen(stroke, paint);
            if (paint.StrokeFirst && fill is not null && penElement is not null)
            {
                output.Add(Drawing(null, penElement));
                output.Add(Drawing(fill, null));
            }
            else
            {
                output.Add(Drawing(fill, penElement));
            }
        }

        private static XElement Pen(object brush, Paint paint)
        {
            var pen = new XElement(W + "Pen", new XAttribute("Thickness", Num.Format(paint.StrokeWidth)));
            if (brush is string s)
            {
                pen.Add(new XAttribute("Brush", s));
            }

            var cap = paint.LineCap switch { "round" => "Round", "square" => "Square", _ => "Flat" };
            if (cap != "Flat")
            {
                pen.Add(new XAttribute("StartLineCap", cap), new XAttribute("EndLineCap", cap));
            }

            var join = paint.LineJoin switch { "round" => "Round", "bevel" => "Bevel", _ => "Miter" };
            if (join != "Miter")
            {
                pen.Add(new XAttribute("LineJoin", join));
            }
            else
            {
                pen.Add(new XAttribute("MiterLimit", Num.Format(paint.MiterLimit)));
            }

            if (brush is XElement b)
            {
                pen.Add(new XElement(W + "Pen.Brush", b));
            }

            return pen;
        }

        /// <summary>A solid color string, a gradient brush element, or null for no paint.</summary>
        private object? Brush(string value, double opacity, string currentColor)
        {
            var v = value.Trim();
            if (v == "none" || opacity <= 0)
            {
                return null;
            }

            if (v == "currentColor")
            {
                v = currentColor.Trim();
            }

            if (v.StartsWith("url(", StringComparison.Ordinal))
            {
                var close = v.IndexOf(')', StringComparison.Ordinal);
                var fallback = close >= 0 ? v[(close + 1)..].Trim() : string.Empty;
                if (_ids.TryGetValue(Url(v) ?? string.Empty, out var target))
                {
                    return Gradient(target, opacity);
                }

                return fallback.Length > 0 ? Brush(fallback, opacity, currentColor) : null;
            }

            return SvgValues.ColorXaml(Recolor(SvgValues.Color(v)), opacity);
        }

        private (byte A, byte R, byte G, byte B) Recolor((byte A, byte R, byte G, byte B) c)
        {
            if (options.Recolor is null)
            {
                return c;
            }

            var r = SvgValues.Color(options.Recolor);
            return (c.A, r.R, r.G, r.B);
        }

        private object? Gradient(XElement target, double opacity)
        {
            var kind = target.Name.LocalName;
            if (kind is not ("linearGradient" or "radialGradient"))
            {
                throw Unsupported("paint server", kind);
            }

            // Attributes and stops may come from gradients this one references (href chain).
            var chain = new List<XElement>();
            for (var g = target; g is not null; g = Href(g) is { } h ? Reference(h) : null)
            {
                if (g.Name.Namespace != Svg || g.Name.LocalName is not ("linearGradient" or "radialGradient"))
                {
                    throw Unsupported("gradient href target", g.Name.LocalName);
                }

                if (chain.Contains(g) || chain.Count >= 16)
                {
                    throw new SvgConversionException("gradient href chain is circular or too long");
                }

                Props(g);
                if (g.Elements().FirstOrDefault(c => c.Name != Svg + "stop" && c.Name.LocalName is not ("title" or "desc")) is { } other)
                {
                    throw new SvgConversionException($"unsupported element <{other.Name.LocalName}> in a gradient");
                }

                chain.Add(g);
            }

            string? Attr(string n) => chain.Select(g => g.Attribute(n)?.Value).FirstOrDefault(v => v is not null);
            var stops = chain.Select(g => g.Elements(Svg + "stop").ToList()).FirstOrDefault(s => s.Count > 0) ?? [];
            if (stops.Count == 0)
            {
                return null;
            }

            var gradientStops = new List<XElement>();
            (byte A, byte R, byte G, byte B)? firstColor = null;
            var firstOpacity = 1.0;
            var last = 0.0;
            foreach (var stop in stops)
            {
                var props = Props(stop);
                var offset = Math.Clamp(SvgValues.Length(stop.Attribute("offset")?.Value ?? "0", 1, "offset"), 0, 1);
                last = Math.Max(last, offset);
                var color = Recolor(SvgValues.Color(props.GetValueOrDefault("stop-color", "black")));
                var stopOpacity = props.TryGetValue("stop-opacity", out var so) ? SvgValues.Opacity(so, "stop-opacity") : 1;
                if (firstColor is null)
                {
                    firstColor = color;
                    firstOpacity = stopOpacity;
                }

                gradientStops.Add(new XElement(W + "GradientStop",
                    new XAttribute("Color", SvgValues.ColorXaml(color, stopOpacity)), new XAttribute("Offset", Num.Format(last))));
            }

            if (gradientStops.Count == 1)
            {
                // One stop paints a solid color.
                return SvgValues.ColorXaml(firstColor!.Value, opacity * firstOpacity);
            }

            var userSpace = (Attr("gradientUnits") ?? "objectBoundingBox") switch
            {
                "userSpaceOnUse" => true,
                "objectBoundingBox" => false,
                var u => throw Unsupported("gradientUnits", u),
            };
            double X(string n, string d) => SvgValues.Length(Attr(n) ?? d, userSpace ? ViewportWidth : 1, n);
            double Y(string n, string d) => SvgValues.Length(Attr(n) ?? d, userSpace ? ViewportHeight : 1, n);

            XElement brush;
            if (kind == "linearGradient")
            {
                brush = new XElement(W + "LinearGradientBrush",
                    new XAttribute("StartPoint", $"{Num.Format(X("x1", "0%"))},{Num.Format(Y("y1", "0%"))}"),
                    new XAttribute("EndPoint", $"{Num.Format(X("x2", "100%"))},{Num.Format(Y("y2", "0%"))}"));
            }
            else
            {
                var cx = X("cx", "50%");
                var cy = Y("cy", "50%");
                var r = SvgValues.Length(Attr("r") ?? "50%", userSpace ? Math.Sqrt((ViewportWidth * ViewportWidth + ViewportHeight * ViewportHeight) / 2) : 1, "r");
                var fx = Attr("fx") is null ? cx : X("fx", "50%");
                var fy = Attr("fy") is null ? cy : Y("fy", "50%");
                brush = new XElement(W + "RadialGradientBrush",
                    new XAttribute("Center", $"{Num.Format(cx)},{Num.Format(cy)}"),
                    new XAttribute("GradientOrigin", $"{Num.Format(fx)},{Num.Format(fy)}"),
                    new XAttribute("RadiusX", Num.Format(r)), new XAttribute("RadiusY", Num.Format(r)));
            }

            if (userSpace)
            {
                brush.Add(new XAttribute("MappingMode", "Absolute"));
            }

            var spread = Attr("spreadMethod") ?? "pad";
            brush.Add(new XAttribute("SpreadMethod", spread switch
            {
                "pad" => "Pad",
                "reflect" => "Reflect",
                "repeat" => "Repeat",
                _ => throw Unsupported("spreadMethod", spread),
            }));

            if (opacity < 1)
            {
                brush.Add(new XAttribute("Opacity", Num.Format(opacity)));
            }

            if (Attr("gradientTransform") is { } gt && SvgValues.Transform(gt) is { IsIdentity: false } matrix)
            {
                // Bounding-box gradients transform in the unit box (RelativeTransform), user-space ones in user space.
                brush.Add(new XAttribute(userSpace ? "Transform" : "RelativeTransform", matrix.ToXaml()));
            }

            brush.Add(gradientStops);
            return brush;
        }

        /// <summary>Geometry element for a basic shape, or null when the shape draws nothing.</summary>
        private XElement? Geometry(XElement e, bool evenOdd, Affine transform)
        {
            XElement? g;
            switch (e.Name.LocalName)
            {
                case "path":
                    var d = e.Attribute("d")?.Value;
                    g = string.IsNullOrWhiteSpace(d) ? null : new XElement(W + "PathGeometry",
                        new XAttribute("FillRule", evenOdd ? "EvenOdd" : "Nonzero"),
                        new XAttribute("Figures", SvgPathData.ToWpf(d, evenOdd)[3..]));
                    break;
                case "rect":
                    var w = Coordinate(e, "width", ViewportWidth);
                    var h = Coordinate(e, "height", ViewportHeight);
                    if (w <= 0 || h <= 0)
                    {
                        return null;
                    }

                    double? rx = e.Attribute("rx") is null ? null : Coordinate(e, "rx", ViewportWidth);
                    double? ry = e.Attribute("ry") is null ? null : Coordinate(e, "ry", ViewportHeight);
                    var radiusX = Math.Clamp(rx ?? ry ?? 0, 0, w / 2);
                    var radiusY = Math.Clamp(ry ?? rx ?? 0, 0, h / 2);
                    g = new XElement(W + "RectangleGeometry",
                        new XAttribute("Rect", $"{Num.Format(Coordinate(e, "x", ViewportWidth))},{Num.Format(Coordinate(e, "y", ViewportHeight))},{Num.Format(w)},{Num.Format(h)}"));
                    if (radiusX > 0 && radiusY > 0)
                    {
                        g.Add(new XAttribute("RadiusX", Num.Format(radiusX)), new XAttribute("RadiusY", Num.Format(radiusY)));
                    }

                    break;
                case "circle":
                case "ellipse":
                    var isCircle = e.Name.LocalName == "circle";
                    var ex = isCircle ? Coordinate(e, "r", Diagonal) : Coordinate(e, "rx", ViewportWidth);
                    var ey = isCircle ? ex : Coordinate(e, "ry", ViewportHeight);
                    if (ex <= 0 || ey <= 0)
                    {
                        return null;
                    }

                    g = new XElement(W + "EllipseGeometry",
                        new XAttribute("Center", $"{Num.Format(Coordinate(e, "cx", ViewportWidth))},{Num.Format(Coordinate(e, "cy", ViewportHeight))}"),
                        new XAttribute("RadiusX", Num.Format(ex)), new XAttribute("RadiusY", Num.Format(ey)));
                    break;
                case "line":
                    g = new XElement(W + "LineGeometry",
                        new XAttribute("StartPoint", $"{Num.Format(Coordinate(e, "x1", ViewportWidth))},{Num.Format(Coordinate(e, "y1", ViewportHeight))}"),
                        new XAttribute("EndPoint", $"{Num.Format(Coordinate(e, "x2", ViewportWidth))},{Num.Format(Coordinate(e, "y2", ViewportHeight))}"));
                    break;
                case "polygon":
                case "polyline":
                    var points = SvgValues.Numbers(e.Attribute("points")?.Value ?? string.Empty, "points");
                    if (points.Count < 4 || points.Count % 2 != 0)
                    {
                        return null;
                    }

                    var figures = new StringBuilder("M");
                    for (var i = 0; i < points.Count; i += 2)
                    {
                        figures.Append(i == 0 ? " " : " L ").Append(Num.Format(points[i])).Append(' ').Append(Num.Format(points[i + 1]));
                    }

                    if (e.Name.LocalName == "polygon")
                    {
                        figures.Append(" Z");
                    }

                    g = new XElement(W + "PathGeometry", new XAttribute("FillRule", evenOdd ? "EvenOdd" : "Nonzero"), new XAttribute("Figures", figures.ToString()));
                    break;
                default:
                    throw new SvgConversionException($"unsupported element <{e.Name.LocalName}>");
            }

            if (g is not null && !transform.IsIdentity)
            {
                g.Add(new XAttribute("Transform", transform.ToXaml()));
            }

            return g;
        }

        private static double Coordinate(XElement e, string name, double reference) =>
            e.Attribute(name) is { } a ? SvgValues.Length(a.Value, reference, name) : 0;

        private XElement Reference(string id) =>
            _ids.TryGetValue(id, out var target) ? target : throw new SvgConversionException($"reference '#{id}' not found");

        private static string? Href(XElement e)
        {
            var value = (e.Attribute("href") ?? e.Attribute(XLink + "href"))?.Value.Trim();
            return value is null ? null : value.StartsWith('#') ? value[1..] : throw Unsupported("href", value);
        }

        private static string? Url(string value)
        {
            var v = value.Trim();
            if (!v.StartsWith("url(", StringComparison.Ordinal))
            {
                return null;
            }

            var close = v.IndexOf(')', StringComparison.Ordinal);
            var inner = (close < 0 ? v[4..] : v[4..close]).Trim().Trim('\'', '"');
            return inner.StartsWith('#') ? inner[1..] : throw Unsupported("url", value);
        }
    }
}
