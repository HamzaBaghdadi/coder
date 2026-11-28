using System.Xml;
using System.Xml.Linq;

namespace Coder.Core.Svg;

/// <summary>
/// A parsed SVG image, ready to draw: every shape is path data with resolved paints, styles and an accumulated
/// transform into document pixels (0..<see cref="Width"/>, 0..<see cref="Height"/>, viewBox already applied).
/// <para>
/// Supported: <c>svg</c> (root and nested), <c>g</c>, <c>a</c>, <c>switch</c> (first child), <c>defs</c>,
/// <c>use</c> (href / xlink:href, x/y, <c>symbol</c> with viewBox), <c>path</c>, <c>rect</c> (rx/ry),
/// <c>circle</c>, <c>ellipse</c>, <c>line</c>, <c>polyline</c>, <c>polygon</c>, <c>text</c>/<c>tspan</c> (first
/// x/y/dx/dy value only), <c>linearGradient</c>/<c>radialGradient</c> (href chains, units, gradientTransform,
/// spreadMethod), presentation attributes, <c>style="..."</c>, and <c>&lt;style&gt;</c> sheets with type, class
/// and #id compound selectors, with inheritance, <c>inherit</c>, <c>!important</c> and specificity.
/// </para>
/// <para>
/// Not supported (skipped without error): <c>clipPath</c>, <c>mask</c>, <c>pattern</c>, <c>marker</c>,
/// <c>filter</c>, <c>image</c>, <c>foreignObject</c>, <c>textPath</c>, <c>xml:space="preserve"</c>, per-glyph
/// position lists, CSS combinators/pseudo-classes, <c>vector-effect</c>, nested viewport clipping, and DTD entity
/// references (the DTD is ignored for safety, so a document that uses its entities fails to parse).
/// </para>
/// </summary>
public sealed class SvgDocument
{
    /// <summary>The largest width or height accepted; bigger values are clamped.</summary>
    public const double MaxSize = 100_000;

    private SvgDocument(double width, double height, SvgViewBox? viewBox, SvgGroup root)
    {
        Width = width;
        Height = height;
        ViewBox = viewBox;
        Root = root;
        Items = [.. Flatten(root.Children)];
    }

    /// <summary>The intrinsic width in px (from <c>width</c>, else the viewBox, else 300).</summary>
    public double Width { get; }

    /// <summary>The intrinsic height in px (from <c>height</c>, else the viewBox, else 150).</summary>
    public double Height { get; }

    public SvgViewBox? ViewBox { get; }

    /// <summary>The render tree, in paint order. Only groups with opacity below 1 appear as <see cref="SvgGroup"/>.</summary>
    public SvgGroup Root { get; }

    /// <summary>Every shape and text in paint order (groups flattened away).</summary>
    public IReadOnlyList<SvgItem> Items { get; }

    /// <summary>
    /// Parses an SVG document. Unknown or malformed elements and attributes are skipped; only input that is not
    /// XML, or whose root is not <c>&lt;svg&gt;</c>, throws (<see cref="SvgException"/>).
    /// </summary>
    public static SvgDocument Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            throw new SvgException("The document is empty.");
        }

        if (xml.Length > SvgParser.MaxCharacters)
        {
            throw new SvgException("The document is too large to preview.");
        }

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore, // No entity expansion (XXE, billion laughs) and no DTD fetching.
                XmlResolver = null,
                MaxCharactersFromEntities = 1024,
                MaxCharactersInDocument = SvgParser.MaxCharacters,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new SvgException($"Not valid XML: {ex.Message}", ex);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "svg" || !SvgParser.IsSvg(root))
        {
            throw new SvgException("The root element is not <svg>.");
        }

        try
        {
            var parser = new SvgParser(root);
            var (width, height, viewBox, tree) = parser.Run();
            return new SvgDocument(width, height, viewBox, tree);
        }
        catch (Exception ex) when (ex is not (SvgException or OutOfMemoryException))
        {
            throw new SvgException($"The SVG could not be read: {ex.Message}", ex);
        }
    }

    private static IEnumerable<SvgItem> Flatten(IEnumerable<SvgItem> items)
    {
        foreach (var item in items)
        {
            if (item is SvgGroup group)
            {
                foreach (var child in Flatten(group.Children))
                {
                    yield return child;
                }
            }
            else
            {
                yield return item;
            }
        }
    }
}

/// <summary>Walks the XML tree, cascading styles and accumulating transforms.</summary>
internal sealed class SvgParser
{
    public const int MaxCharacters = 64 * 1024 * 1024;
    private const int MaxItems = 50_000;
    private const int MaxVisits = 200_000;
    private const int MaxDepth = 128;
    private const int MaxUseDepth = 16;

    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLinkNs = "http://www.w3.org/1999/xlink";

    private static readonly HashSet<string> NonRendered = new(StringComparer.Ordinal)
    {
        "defs", "symbol", "clipPath", "mask", "pattern", "marker", "linearGradient", "radialGradient", "style",
        "title", "desc", "metadata", "script", "filter", "foreignObject", "image", "stop", "tspan", "textPath",
    };

    private static readonly HashSet<string> Properties = new(StringComparer.Ordinal)
    {
        "fill", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray",
        "stroke-dashoffset", "fill-rule", "opacity", "fill-opacity", "stroke-opacity", "color", "font-size",
        "font-family", "font-weight", "font-style", "text-anchor", "display", "visibility", "stop-color", "stop-opacity",
    };

    private readonly XElement _root;
    private readonly Dictionary<string, XElement> _ids = new(StringComparer.Ordinal);
    private readonly List<SvgCssRule> _rules = [];
    private readonly Dictionary<string, SvgGradient?> _gradients = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolvingGradients = new(StringComparer.Ordinal);
    private readonly HashSet<XElement> _useStack = [];
    private readonly Stack<(double Width, double Height)> _viewports = new();
    private int _items;
    private int _visits;

    public SvgParser(XElement root) => _root = root;

    public static bool IsSvg(XElement element) => element.Name.Namespace == SvgNs || element.Name.Namespace == XNamespace.None;

    public (double Width, double Height, SvgViewBox? ViewBox, SvgGroup Root) Run()
    {
        Index();

        var viewBox = ParseViewBox(_root.Attribute("viewBox")?.Value);
        var width = RootLength(_root.Attribute("width")?.Value);
        var height = RootLength(_root.Attribute("height")?.Value);
        if (width is null && height is null)
        {
            width = viewBox?.Width ?? 300;
            height = viewBox?.Height ?? 150;
        }
        else if (width is null)
        {
            width = viewBox is { } vb ? height!.Value * vb.Width / vb.Height : 300;
        }
        else if (height is null)
        {
            height = viewBox is { } vb ? width.Value * vb.Height / vb.Width : 150;
        }

        var w = Math.Clamp(width.Value, 0, SvgDocument.MaxSize);
        var h = Math.Clamp(height!.Value, 0, SvgDocument.MaxSize);

        _viewports.Push(viewBox is { } box ? (box.Width, box.Height) : (w, h));
        var style = ComputeStyle(_root, new Style());
        var transform = LocalTransform(_root).Then(ViewBoxTransform(viewBox, _root.Attribute("preserveAspectRatio")?.Value, 0, 0, w, h));

        var children = new List<SvgItem>();
        if (!style.DisplayNone && style.Opacity > 0)
        {
            WalkChildren(_root.Elements(), style, transform, children, 1);
        }

        var root = new SvgGroup { ElementName = "svg", Id = _root.Attribute("id")?.Value, Opacity = style.Opacity, Children = children };
        return (w, h, viewBox, root);
    }

    // ---- Pre-pass: ids and style sheets ----

    private void Index()
    {
        var order = 0;
        var visits = 0;
        foreach (var element in _root.DescendantsAndSelf())
        {
            if (++visits > MaxVisits)
            {
                break;
            }

            if (element.Attribute("id")?.Value is { Length: > 0 } id)
            {
                _ids.TryAdd(id, element);
            }

            if (element.Name.LocalName == "style" && IsSvg(element)
                && element.Attribute("type")?.Value is null or "" or "text/css")
            {
                SvgCss.ParseSheet(element.Value, _rules, ref order);
            }
        }
    }

    // ---- Tree walk ----

    private void WalkChildren(IEnumerable<XElement> children, Style style, SvgMatrix transform, List<SvgItem> output, int depth)
    {
        foreach (var child in children)
        {
            Walk(child, style, transform, output, depth);
        }
    }

    private void Walk(XElement element, Style parentStyle, SvgMatrix parentTransform, List<SvgItem> output, int depth)
    {
        if (!IsSvg(element) || depth > MaxDepth || ++_visits > MaxVisits || _items >= MaxItems)
        {
            return;
        }

        var name = element.Name.LocalName;
        if (NonRendered.Contains(name))
        {
            return;
        }

        try
        {
            var style = ComputeStyle(element, parentStyle);
            if (style.DisplayNone || style.Opacity <= 0)
            {
                return;
            }

            var transform = LocalTransform(element).Then(parentTransform);
            switch (name)
            {
                case "g":
                case "a":
                    Container(element, element.Elements(), style, transform, output, depth);
                    break;
                case "switch":
                    Container(element, element.Elements().Where(IsSvg).Take(1), style, transform, output, depth);
                    break;
                case "svg":
                    NestedViewport(element, style, transform, output, depth);
                    break;
                case "use":
                    Use(element, style, transform, output, depth);
                    break;
                case "text":
                    Text(element, style, transform, output);
                    break;
                default:
                    Shape(element, name, style, transform, output);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // One malformed element must not lose the rest of the drawing.
        }
    }

    /// <summary>Walks children; when the container is translucent they are collected into an <see cref="SvgGroup"/>.</summary>
    private void Container(XElement element, IEnumerable<XElement> children, Style style, SvgMatrix transform, List<SvgItem> output, int depth)
    {
        if (style.Opacity >= 1)
        {
            WalkChildren(children, style, transform, output, depth + 1);
            return;
        }

        var list = new List<SvgItem>();
        WalkChildren(children, style.ForChildren(), transform, list, depth + 1);
        if (list.Count > 0)
        {
            output.Add(new SvgGroup
            {
                ElementName = element.Name.LocalName, Id = element.Attribute("id")?.Value,
                Transform = transform, Opacity = style.Opacity, Children = list,
            });
        }
    }

    private void NestedViewport(XElement element, Style style, SvgMatrix transform, List<SvgItem> output, int depth)
    {
        var (vw, vh) = _viewports.Peek();
        var x = Length(element, "x", style, vw, 0);
        var y = Length(element, "y", style, vh, 0);
        var w = Length(element, "width", style, vw, vw);
        var h = Length(element, "height", style, vh, vh);
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var viewBox = ParseViewBox(element.Attribute("viewBox")?.Value);
        var mapping = ViewBoxTransform(viewBox, element.Attribute("preserveAspectRatio")?.Value, x, y, w, h);
        _viewports.Push(viewBox is { } box ? (box.Width, box.Height) : (w, h));
        try
        {
            Container(element, element.Elements(), style, mapping.Then(transform), output, depth);
        }
        finally
        {
            _viewports.Pop();
        }
    }

    private void Use(XElement element, Style style, SvgMatrix transform, List<SvgItem> output, int depth)
    {
        if (Href(element) is not { } id || !_ids.TryGetValue(id, out var target) || !IsSvg(target)
            || _useStack.Count >= MaxUseDepth || element.AncestorsAndSelf().Contains(target) || !_useStack.Add(target))
        {
            return;
        }

        try
        {
            var (vw, vh) = _viewports.Peek();
            var placed = SvgMatrix.Translate(Length(element, "x", style, vw, 0), Length(element, "y", style, vh, 0)).Then(transform);
            var list = new List<SvgItem>();
            var childStyle = style.Opacity >= 1 ? style : style.ForChildren();

            if (target.Name.LocalName == "symbol")
            {
                var symbolStyle = ComputeStyle(target, childStyle);
                if (!symbolStyle.DisplayNone)
                {
                    var w = Length(element, "width", style, vw, vw);
                    var h = Length(element, "height", style, vh, vh);
                    var viewBox = ParseViewBox(target.Attribute("viewBox")?.Value);
                    var mapping = ViewBoxTransform(viewBox, target.Attribute("preserveAspectRatio")?.Value, 0, 0, w, h);
                    _viewports.Push(viewBox is { } box ? (box.Width, box.Height) : (w, h));
                    try
                    {
                        Container(target, target.Elements(), symbolStyle, LocalTransform(target).Then(mapping).Then(placed), list, depth);
                    }
                    finally
                    {
                        _viewports.Pop();
                    }
                }
            }
            else
            {
                Walk(target, childStyle, placed, list, depth + 1);
            }

            if (style.Opacity >= 1)
            {
                output.AddRange(list);
            }
            else if (list.Count > 0)
            {
                output.Add(new SvgGroup { ElementName = "use", Id = element.Attribute("id")?.Value, Transform = transform, Opacity = style.Opacity, Children = list });
            }
        }
        finally
        {
            _useStack.Remove(target);
        }
    }

    private void Shape(XElement element, string name, Style style, SvgMatrix transform, List<SvgItem> output)
    {
        if (!style.Visible)
        {
            return;
        }

        var (vw, vh) = _viewports.Peek();
        var diagonal = Diagonal(vw, vh);
        string data;
        switch (name)
        {
            case "path":
                data = element.Attribute("d")?.Value ?? "";
                break;
            case "rect":
                var rx = OptionalLength(element, "rx", style, vw);
                var ry = OptionalLength(element, "ry", style, vh);
                data = SvgPathData.Rectangle(
                    Length(element, "x", style, vw, 0), Length(element, "y", style, vh, 0),
                    Length(element, "width", style, vw, 0), Length(element, "height", style, vh, 0),
                    rx ?? ry ?? 0, ry ?? rx ?? 0);
                break;
            case "circle":
                var r = Length(element, "r", style, diagonal, 0);
                data = SvgPathData.Ellipse(Length(element, "cx", style, vw, 0), Length(element, "cy", style, vh, 0), r, r);
                break;
            case "ellipse":
                var erx = OptionalLength(element, "rx", style, vw);
                var ery = OptionalLength(element, "ry", style, vh);
                data = SvgPathData.Ellipse(Length(element, "cx", style, vw, 0), Length(element, "cy", style, vh, 0), erx ?? ery ?? 0, ery ?? erx ?? 0);
                break;
            case "line":
                data = SvgPathData.Line(
                    Length(element, "x1", style, vw, 0), Length(element, "y1", style, vh, 0),
                    Length(element, "x2", style, vw, 0), Length(element, "y2", style, vh, 0));
                break;
            case "polyline":
            case "polygon":
                data = SvgPathData.Polyline(SvgValues.NumberList(element.Attribute("points")?.Value), name == "polygon");
                break;
            default:
                return; // Unknown element: skipped with its children.
        }

        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        output.Add(new SvgShape
        {
            ElementName = name,
            Id = element.Attribute("id")?.Value,
            Transform = transform,
            Opacity = style.Opacity,
            PathData = data,
            Fill = ResolvePaint(style.Fill, style),
            Stroke = ResolvePaint(style.Stroke, style),
            FillOpacity = style.FillOpacity,
            StrokeOpacity = style.StrokeOpacity,
            StrokeWidth = style.StrokeWidth,
            StrokeLineCap = style.LineCap,
            StrokeLineJoin = style.LineJoin,
            StrokeMiterLimit = style.MiterLimit,
            StrokeDashArray = style.DashArray ?? [],
            StrokeDashOffset = style.DashOffset,
            FillRule = style.FillRule,
        });
        _items++;
    }

    // ---- Text ----

    private void Text(XElement element, Style style, SvgMatrix transform, List<SvgItem> output)
    {
        var (vw, vh) = _viewports.Peek();
        var runs = new List<SvgTextRun>();
        var pending = new PendingPosition
        {
            Dx = FirstLength(element, "dx", style, vw) ?? 0,
            Dy = FirstLength(element, "dy", style, vh) ?? 0,
        };
        CollectRuns(element, style, runs, pending);

        // Collapse whitespace as CSS does by default: runs of spaces become one, the ends are trimmed.
        for (var i = 0; i < runs.Count; i++)
        {
            var text = runs[i].Text;
            if (i == 0 || runs[i - 1].Text.EndsWith(' '))
            {
                text = text.TrimStart(' ');
            }

            if (i == runs.Count - 1)
            {
                text = text.TrimEnd(' ');
            }

            if (text != runs[i].Text)
            {
                runs[i] = Copy(runs[i], text);
            }
        }

        runs.RemoveAll(r => r.Text.Length == 0);
        if (runs.Count == 0 || runs.All(r => !r.Text.Any(c => c != ' ')))
        {
            return;
        }

        output.Add(new SvgText
        {
            ElementName = "text",
            Id = element.Attribute("id")?.Value,
            Transform = transform,
            Opacity = style.Opacity,
            X = FirstLength(element, "x", style, vw) ?? 0,
            Y = FirstLength(element, "y", style, vh) ?? 0,
            Runs = runs,
        });
        _items++;
    }

    private void CollectRuns(XElement element, Style style, List<SvgTextRun> runs, PendingPosition pending)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                var collapsed = Collapse(text.Value);
                if (collapsed.Length == 0 || !style.Visible)
                {
                    continue;
                }

                runs.Add(new SvgTextRun
                {
                    Text = collapsed,
                    X = pending.X,
                    Y = pending.Y,
                    Dx = pending.Dx,
                    Dy = pending.Dy,
                    FontSize = style.FontSize,
                    FontFamily = style.FontFamily,
                    FontWeight = style.FontWeight,
                    Italic = style.Italic,
                    TextAnchor = style.Anchor,
                    Fill = ResolvePaint(style.Fill, style),
                    FillOpacity = style.FillOpacity,
                });
                pending.X = pending.Y = null;
                pending.Dx = pending.Dy = 0;
            }
            else if (node is XElement child && IsSvg(child) && child.Name.LocalName is "tspan" or "a" && ++_visits < MaxVisits)
            {
                var childStyle = ComputeStyle(child, style);
                if (childStyle.DisplayNone)
                {
                    continue;
                }

                var (vw, vh) = _viewports.Peek();
                pending.X = FirstLength(child, "x", childStyle, vw) ?? pending.X;
                pending.Y = FirstLength(child, "y", childStyle, vh) ?? pending.Y;
                pending.Dx += FirstLength(child, "dx", childStyle, vw) ?? 0;
                pending.Dy += FirstLength(child, "dy", childStyle, vh) ?? 0;
                CollectRuns(child, childStyle, runs, pending);
            }
        }
    }

    private static string Collapse(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            var ch = char.IsWhiteSpace(c) ? ' ' : c;
            if (ch == ' ' && builder.Length > 0 && builder[^1] == ' ')
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static SvgTextRun Copy(SvgTextRun run, string text) => new()
    {
        Text = text, X = run.X, Y = run.Y, Dx = run.Dx, Dy = run.Dy, FontSize = run.FontSize, FontFamily = run.FontFamily,
        FontWeight = run.FontWeight, Italic = run.Italic, TextAnchor = run.TextAnchor, Fill = run.Fill, FillOpacity = run.FillOpacity,
    };

    private sealed class PendingPosition
    {
        public double? X { get; set; }

        public double? Y { get; set; }

        public double Dx { get; set; }

        public double Dy { get; set; }
    }

    // ---- Paints and gradients ----

    private SvgPaint ResolvePaint(PaintSpec spec, Style style)
    {
        switch (spec.Kind)
        {
            case PaintKind.None:
                return SvgPaint.None;
            case PaintKind.Color:
                return SvgPaint.Solid(spec.Color);
            case PaintKind.CurrentColor:
                return SvgPaint.Solid(style.Color);
            default:
                var gradient = Gradient(spec.Url!);
                if (gradient is null)
                {
                    return spec.Fallback is { } fallback ? ResolvePaint(fallback, style) : SvgPaint.None;
                }

                return gradient.Stops.Count switch
                {
                    0 => SvgPaint.None,
                    1 => SvgPaint.Solid(gradient.Stops[0].Color),
                    _ => SvgPaint.From(gradient),
                };
        }
    }

    private SvgGradient? Gradient(string id)
    {
        if (_gradients.TryGetValue(id, out var cached))
        {
            return cached;
        }

        if (!_resolvingGradients.Add(id))
        {
            return null;
        }

        try
        {
            var gradient = BuildGradient(id);
            _gradients[id] = gradient;
            return gradient;
        }
        finally
        {
            _resolvingGradients.Remove(id);
        }
    }

    private SvgGradient? BuildGradient(string id)
    {
        if (!_ids.TryGetValue(id, out var element) || !IsSvg(element)
            || element.Name.LocalName is not ("linearGradient" or "radialGradient"))
        {
            return null;
        }

        // The href chain: attributes and stops not given here come from the referenced gradient.
        var chain = new List<XElement> { element };
        var current = element;
        while (chain.Count < 16 && Href(current) is { } next && _ids.TryGetValue(next, out var referenced)
            && referenced.Name.LocalName is "linearGradient" or "radialGradient" && !chain.Contains(referenced))
        {
            chain.Add(referenced);
            current = referenced;
        }

        string? Attr(string name) => chain.Select(e => e.Attribute(name)?.Value).FirstOrDefault(v => v is not null);

        var units = Attr("gradientUnits") == "userSpaceOnUse" ? SvgGradientUnits.UserSpaceOnUse : SvgGradientUnits.ObjectBoundingBox;
        SvgValues.TryTransform(Attr("gradientTransform"), out var transform);
        var spread = Attr("spreadMethod") switch
        {
            "reflect" => SvgSpreadMethod.Reflect,
            "repeat" => SvgSpreadMethod.Repeat,
            _ => SvgSpreadMethod.Pad,
        };

        var (vw, vh) = _viewports.Count > 0 ? _viewports.Peek() : (100, 100);
        var user = units == SvgGradientUnits.UserSpaceOnUse;
        double Coordinate(string name, double percentBase, double fallback) =>
            SvgValues.TryLength(Attr(name), 16, user ? percentBase : 1, out var value) ? value : fallback;

        var stopsElement = chain.FirstOrDefault(e => e.Elements().Any(s => s.Name.LocalName == "stop"));
        var stops = new List<SvgGradientStop>();
        if (stopsElement is not null)
        {
            var gradientStyle = ComputeStyle(stopsElement, new Style());
            var last = 0.0;
            foreach (var stop in stopsElement.Elements().Where(s => s.Name.LocalName == "stop" && IsSvg(s)))
            {
                var stopStyle = ComputeStyle(stop, gradientStyle);
                var offset = SvgValues.TryLength(stop.Attribute("offset")?.Value, 16, 1, out var o) ? Math.Clamp(o, 0, 1) : 0;
                last = Math.Max(last, offset);
                var color = stopStyle.StopColor is { } spec
                    ? spec.Equals("currentColor", StringComparison.OrdinalIgnoreCase) ? stopStyle.Color
                        : SvgColors.TryParse(spec, out var parsed) ? parsed : SvgColor.Black
                    : SvgColor.Black;
                color = color with { A = (byte)Math.Round(color.A * stopStyle.StopOpacity) };
                stops.Add(new(last, color));
            }
        }

        if (element.Name.LocalName == "linearGradient")
        {
            return new SvgLinearGradient(id, stops, units, transform, spread,
                Coordinate("x1", vw, 0), Coordinate("y1", vh, 0), Coordinate("x2", vw, user ? vw : 1), Coordinate("y2", vh, 0));
        }

        var diagonal = Diagonal(vw, vh);
        var cx = Coordinate("cx", vw, user ? vw / 2 : 0.5);
        var cy = Coordinate("cy", vh, user ? vh / 2 : 0.5);
        var r = Coordinate("r", diagonal, user ? diagonal / 2 : 0.5);
        return new SvgRadialGradient(id, stops, units, transform, spread, cx, cy, r, Coordinate("fx", vw, cx), Coordinate("fy", vh, cy));
    }

    // ---- Styles ----

    private Style ComputeStyle(XElement element, Style parent)
    {
        var style = parent.ForChildren();
        var declarations = new List<(int Level, int Specificity, int Order, SvgDeclaration Declaration)>();
        var order = 0;

        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name.Namespace == XNamespace.None && Properties.Contains(attribute.Name.LocalName))
            {
                declarations.Add((0, 0, order++, new(attribute.Name.LocalName, attribute.Value.Trim(), false)));
            }
        }

        if (_rules.Count > 0)
        {
            var id = element.Attribute("id")?.Value;
            var classes = (element.Attribute("class")?.Value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rule in _rules)
            {
                if (rule.Matches(element.Name.LocalName, id, classes))
                {
                    foreach (var declaration in rule.Declarations)
                    {
                        declarations.Add((declaration.Important ? 3 : 1, rule.Specificity, rule.Order, declaration));
                    }
                }
            }
        }

        foreach (var declaration in SvgCss.ParseDeclarations(element.Attribute("style")?.Value))
        {
            declarations.Add((declaration.Important ? 4 : 2, 0, order++, declaration));
        }

        // font-size first, so em lengths in the same element use the new size.
        foreach (var (_, _, _, declaration) in declarations
            .OrderBy(d => d.Declaration.Name == "font-size" ? 0 : 1)
            .ThenBy(d => d.Level).ThenBy(d => d.Specificity).ThenBy(d => d.Order))
        {
            Apply(style, parent, declaration.Name, declaration.Value);
        }

        return style;
    }

    private void Apply(Style style, Style parent, string name, string value)
    {
        if (value.Equals("inherit", StringComparison.OrdinalIgnoreCase))
        {
            style.CopyFrom(parent, name);
            return;
        }

        var (vw, vh) = _viewports.Count > 0 ? _viewports.Peek() : (100, 100);
        switch (name)
        {
            case "fill":
                style.Fill = ParsePaint(value) ?? style.Fill;
                break;
            case "stroke":
                style.Stroke = ParsePaint(value) ?? style.Stroke;
                break;
            case "stroke-width":
                if (SvgValues.TryLength(value, style.FontSize, Diagonal(vw, vh), out var width) && width >= 0)
                {
                    style.StrokeWidth = width;
                }

                break;
            case "stroke-linecap":
                style.LineCap = value switch { "round" => SvgLineCap.Round, "square" => SvgLineCap.Square, "butt" => SvgLineCap.Butt, _ => style.LineCap };
                break;
            case "stroke-linejoin":
                style.LineJoin = value switch { "round" => SvgLineJoin.Round, "bevel" => SvgLineJoin.Bevel, "miter" or "miter-clip" or "arcs" => SvgLineJoin.Miter, _ => style.LineJoin };
                break;
            case "stroke-miterlimit":
                if (SvgValues.TryNumber(value, out var miter) && miter >= 1)
                {
                    style.MiterLimit = miter;
                }

                break;
            case "stroke-dasharray":
                style.DashArray = value == "none" ? null : ParseDashArray(value, style.FontSize, Diagonal(vw, vh)) ?? style.DashArray;
                break;
            case "stroke-dashoffset":
                if (SvgValues.TryLength(value, style.FontSize, Diagonal(vw, vh), out var dashOffset))
                {
                    style.DashOffset = dashOffset;
                }

                break;
            case "fill-rule":
                style.FillRule = value switch { "evenodd" => SvgFillRule.EvenOdd, "nonzero" => SvgFillRule.NonZero, _ => style.FillRule };
                break;
            case "opacity":
                style.Opacity = ParseOpacity(value) ?? style.Opacity;
                break;
            case "fill-opacity":
                style.FillOpacity = ParseOpacity(value) ?? style.FillOpacity;
                break;
            case "stroke-opacity":
                style.StrokeOpacity = ParseOpacity(value) ?? style.StrokeOpacity;
                break;
            case "stop-opacity":
                style.StopOpacity = ParseOpacity(value) ?? style.StopOpacity;
                break;
            case "stop-color":
                style.StopColor = value;
                break;
            case "color":
                if (SvgColors.TryParse(value, out var color))
                {
                    style.Color = color;
                }

                break;
            case "font-size":
                style.FontSize = ParseFontSize(value, parent.FontSize) ?? style.FontSize;
                break;
            case "font-family":
                var families = value.Split(',').Select(f => f.Trim().Trim('"', '\'').Trim()).Where(f => f.Length > 0).ToList();
                if (families.Count > 0)
                {
                    style.FontFamily = string.Join(", ", families);
                }

                break;
            case "font-weight":
                style.FontWeight = value switch
                {
                    "normal" => 400,
                    "bold" => 700,
                    "bolder" => parent.FontWeight < 400 ? 400 : parent.FontWeight < 600 ? 700 : 900,
                    "lighter" => parent.FontWeight < 600 ? 100 : parent.FontWeight < 800 ? 400 : 700,
                    _ => SvgValues.TryNumber(value, out var weight) && weight is >= 1 and <= 1000 ? (int)weight : style.FontWeight,
                };
                break;
            case "font-style":
                style.Italic = value is "italic" or "oblique" || (value != "normal" && style.Italic);
                break;
            case "text-anchor":
                style.Anchor = value switch { "middle" => SvgTextAnchor.Middle, "end" => SvgTextAnchor.End, "start" => SvgTextAnchor.Start, _ => style.Anchor };
                break;
            case "display":
                style.DisplayNone = value == "none";
                break;
            case "visibility":
                style.Visible = value switch { "hidden" or "collapse" => false, "visible" => true, _ => style.Visible };
                break;
            default:
                break;
        }
    }

    private static PaintSpec? ParsePaint(string value)
    {
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return PaintSpec.NoPaint;
        }

        if (value.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            return new PaintSpec(PaintKind.CurrentColor, SvgColor.Black, null, null);
        }

        if (value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            var close = value.IndexOf(')', StringComparison.Ordinal);
            if (close < 0)
            {
                return null;
            }

            var reference = value[4..close].Trim().Trim('"', '\'').Trim();
            var hash = reference.IndexOf('#', StringComparison.Ordinal);
            if (hash < 0 || hash == reference.Length - 1)
            {
                return null;
            }

            var rest = value[(close + 1)..].Trim();
            return new PaintSpec(PaintKind.Url, SvgColor.Black, reference[(hash + 1)..], rest.Length > 0 ? ParsePaint(rest) : null);
        }

        return SvgColors.TryParse(value, out var color) ? new PaintSpec(PaintKind.Color, color, null, null) : null;
    }

    private static double? ParseOpacity(string value)
    {
        var percent = value.EndsWith('%');
        return SvgValues.TryNumber(percent ? value[..^1] : value, out var number)
            ? Math.Clamp(percent ? number / 100 : number, 0, 1)
            : null;
    }

    private static double? ParseFontSize(string value, double parentSize)
    {
        double? size = value switch
        {
            "xx-small" => 9,
            "x-small" => 10,
            "small" => 13,
            "medium" => 16,
            "large" => 18,
            "x-large" => 24,
            "xx-large" => 32,
            "larger" => parentSize * 1.2,
            "smaller" => parentSize / 1.2,
            _ => SvgValues.TryLength(value, parentSize, parentSize, out var length) && length >= 0 ? length : null,
        };
        return size is { } s ? Math.Min(s, 10_000) : null;
    }

    private static List<double>? ParseDashArray(string value, double fontSize, double percentBase)
    {
        var dashes = new List<double>();
        foreach (var part in value.Split([',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!SvgValues.TryLength(part, fontSize, percentBase, out var dash) || dash < 0)
            {
                return null; // An invalid list is ignored, as if not specified.
            }

            dashes.Add(dash);
        }

        if (dashes.Count == 0 || dashes.Sum() <= 0)
        {
            return [];
        }

        if (dashes.Count % 2 == 1)
        {
            dashes.AddRange(dashes.ToList());
        }

        return dashes;
    }

    // ---- Geometry helpers ----

    private static SvgMatrix LocalTransform(XElement element) =>
        SvgValues.TryTransform(element.Attribute("transform")?.Value, out var matrix) ? matrix : SvgMatrix.Identity;

    private static double Diagonal(double width, double height) => Math.Sqrt(((width * width) + (height * height)) / 2);

    private static double Length(XElement element, string name, Style style, double percentBase, double fallback) =>
        SvgValues.TryLength(element.Attribute(name)?.Value, style.FontSize, percentBase, out var value) ? value : fallback;

    private static double? OptionalLength(XElement element, string name, Style style, double percentBase) =>
        SvgValues.TryLength(element.Attribute(name)?.Value, style.FontSize, percentBase, out var value) && value >= 0 ? value : null;

    /// <summary>The first value of a length list such as a text's <c>x="10 20 30"</c>.</summary>
    private static double? FirstLength(XElement element, string name, Style style, double percentBase)
    {
        var first = element.Attribute(name)?.Value.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return SvgValues.TryLength(first, style.FontSize, percentBase, out var value) ? value : null;
    }

    private static double? RootLength(string? value) =>
        value is not null && !value.TrimEnd().EndsWith('%') && SvgValues.TryLength(value, 16, 0, out var length) && length > 0
            ? length
            : null;

    private static SvgViewBox? ParseViewBox(string? value)
    {
        var numbers = SvgValues.NumberList(value);
        return numbers.Count == 4 && numbers[2] > 0 && numbers[3] > 0 && numbers[2] <= 1e9 && numbers[3] <= 1e9
            ? new SvgViewBox(numbers[0], numbers[1], numbers[2], numbers[3])
            : null;
    }

    /// <summary>The viewBox to viewport mapping, honoring <c>preserveAspectRatio</c> (default xMidYMid meet).</summary>
    internal static SvgMatrix ViewBoxTransform(SvgViewBox? viewBox, string? preserveAspectRatio, double x, double y, double width, double height)
    {
        if (viewBox is not { } box)
        {
            return SvgMatrix.Translate(x, y);
        }

        var sx = width / box.Width;
        var sy = height / box.Height;
        var tokens = (preserveAspectRatio ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(t => t != "defer").ToList();
        var align = tokens.Count > 0 ? tokens[0] : "xMidYMid";
        if (align == "none")
        {
            return SvgMatrix.Translate(-box.X, -box.Y).Then(SvgMatrix.Scale(sx, sy)).Then(SvgMatrix.Translate(x, y));
        }

        var slice = tokens.Count > 1 && tokens[1] == "slice";
        var scale = slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
        double Factor(string axis) =>
            align.Contains(axis + "Min", StringComparison.Ordinal) ? 0
            : align.Contains(axis + "Max", StringComparison.Ordinal) ? 1
            : 0.5;

        var tx = x + ((width - (box.Width * scale)) * Factor("x"));
        var ty = y + ((height - (box.Height * scale)) * Factor("Y"));
        return SvgMatrix.Translate(-box.X, -box.Y).Then(SvgMatrix.Scale(scale, scale)).Then(SvgMatrix.Translate(tx, ty));
    }

    private static string? Href(XElement element)
    {
        var href = element.Attribute("href")?.Value ?? element.Attribute(XLinkNs + "href")?.Value;
        href = href?.Trim();
        return href is { Length: > 1 } && href[0] == '#' ? href[1..] : null;
    }

    // ---- Cascaded style state ----

    private enum PaintKind
    {
        None,
        Color,
        CurrentColor,
        Url,
    }

    private sealed record PaintSpec(PaintKind Kind, SvgColor Color, string? Url, PaintSpec? Fallback)
    {
        public static readonly PaintSpec NoPaint = new(PaintKind.None, SvgColor.Transparent, null, null);
        public static readonly PaintSpec BlackPaint = new(PaintKind.Color, SvgColor.Black, null, null);
    }

    private sealed class Style
    {
        // Inherited properties.
        public PaintSpec Fill { get; set; } = PaintSpec.BlackPaint;

        public PaintSpec Stroke { get; set; } = PaintSpec.NoPaint;

        public double FillOpacity { get; set; } = 1;

        public double StrokeOpacity { get; set; } = 1;

        public double StrokeWidth { get; set; } = 1;

        public SvgLineCap LineCap { get; set; }

        public SvgLineJoin LineJoin { get; set; }

        public double MiterLimit { get; set; } = 4;

        public List<double>? DashArray { get; set; }

        public double DashOffset { get; set; }

        public SvgFillRule FillRule { get; set; }

        public SvgColor Color { get; set; } = SvgColor.Black;

        public double FontSize { get; set; } = 16;

        public string FontFamily { get; set; } = "sans-serif";

        public int FontWeight { get; set; } = 400;

        public bool Italic { get; set; }

        public SvgTextAnchor Anchor { get; set; }

        public bool Visible { get; set; } = true;

        // Not inherited.
        public double Opacity { get; set; } = 1;

        public bool DisplayNone { get; set; }

        public string? StopColor { get; set; }

        public double StopOpacity { get; set; } = 1;

        /// <summary>A copy with the inherited properties kept and the others reset.</summary>
        public Style ForChildren()
        {
            var copy = (Style)MemberwiseClone();
            copy.Opacity = 1;
            copy.DisplayNone = false;
            copy.StopColor = null;
            copy.StopOpacity = 1;
            return copy;
        }

        public void CopyFrom(Style parent, string name)
        {
            switch (name)
            {
                case "fill": Fill = parent.Fill; break;
                case "stroke": Stroke = parent.Stroke; break;
                case "stroke-width": StrokeWidth = parent.StrokeWidth; break;
                case "stroke-linecap": LineCap = parent.LineCap; break;
                case "stroke-linejoin": LineJoin = parent.LineJoin; break;
                case "stroke-miterlimit": MiterLimit = parent.MiterLimit; break;
                case "stroke-dasharray": DashArray = parent.DashArray; break;
                case "stroke-dashoffset": DashOffset = parent.DashOffset; break;
                case "fill-rule": FillRule = parent.FillRule; break;
                case "opacity": Opacity = parent.Opacity; break;
                case "fill-opacity": FillOpacity = parent.FillOpacity; break;
                case "stroke-opacity": StrokeOpacity = parent.StrokeOpacity; break;
                case "color": Color = parent.Color; break;
                case "font-size": FontSize = parent.FontSize; break;
                case "font-family": FontFamily = parent.FontFamily; break;
                case "font-weight": FontWeight = parent.FontWeight; break;
                case "font-style": Italic = parent.Italic; break;
                case "text-anchor": Anchor = parent.Anchor; break;
                case "visibility": Visible = parent.Visible; break;
                case "stop-color": StopColor = parent.StopColor; break;
                case "stop-opacity": StopOpacity = parent.StopOpacity; break;
                default: break;
            }
        }
    }
}
