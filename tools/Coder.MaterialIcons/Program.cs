using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

// Converts the Material Icon Theme package (https://github.com/material-extensions/vscode-material-icon-theme,
// MIT) into Coder's assets, so the editor ships the icons without an SVG library:
//
//   icons.txt      one line per icon: name <TAB> viewBox <TAB> layers separated by ';'
//                  fill layer:   F|color|opacity|n or e (fill rule)|matrix|path data
//                  stroke layer: S|color|opacity|width,cap,join|matrix|path data
//   manifest.json  file name, extension, folder and language mappings to icon names (+ light variants)
//   LICENSE        the package's license, which travels with the icons
//
// Usage: dotnet run --project tools/Coder.MaterialIcons -- <unpacked npm package folder> assets/material-icons
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Coder.MaterialIcons <package folder> <output folder>");
    return 1;
}

var package = args[0];
var output = args[1];
Directory.CreateDirectory(output);

var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(package, "dist", "material-icons.json")))!.AsObject();
var definitions = manifest["iconDefinitions"]!.AsObject();

// Icon ids → svg file base names (they almost always match, but the definitions are the truth).
string IconName(string id) => definitions[id] is { } definition
    ? Path.GetFileNameWithoutExtension(definition["iconPath"]!.GetValue<string>())
    : id;

JsonObject Map(JsonNode? source)
{
    var map = new JsonObject();
    foreach (var (key, value) in source?.AsObject() ?? [])
    {
        map[key] = IconName(value!.GetValue<string>());
    }

    return map;
}

var light = manifest["light"]?.AsObject();
var compact = new JsonObject
{
    ["file"] = IconName(manifest["file"]!.GetValue<string>()),
    ["folder"] = IconName(manifest["folder"]!.GetValue<string>()),
    ["folderExpanded"] = IconName(manifest["folderExpanded"]!.GetValue<string>()),
    ["fileExtensions"] = Map(manifest["fileExtensions"]),
    ["fileNames"] = Map(manifest["fileNames"]),
    ["folderNames"] = Map(manifest["folderNames"]),
    ["folderNamesExpanded"] = Map(manifest["folderNamesExpanded"]),
    ["languageIds"] = Map(manifest["languageIds"]),
    ["light"] = new JsonObject
    {
        ["fileExtensions"] = Map(light?["fileExtensions"]),
        ["fileNames"] = Map(light?["fileNames"]),
        ["folderNames"] = Map(light?["folderNames"]),
        ["folderNamesExpanded"] = Map(light?["folderNamesExpanded"]),
    },
};
File.WriteAllText(Path.Combine(output, "manifest.json"), compact.ToJsonString());

var lines = new StringBuilder();
var skipped = new List<string>();
foreach (var file in Directory.EnumerateFiles(Path.Combine(package, "icons"), "*.svg").Order(StringComparer.Ordinal))
{
    var name = Path.GetFileNameWithoutExtension(file);
    try
    {
        var (viewBox, layers) = SvgConverter.Convert(XDocument.Load(file));
        if (layers.Count == 0)
        {
            skipped.Add(name);
            continue;
        }

        lines.Append(name).Append('\t').Append(viewBox).Append('\t').AppendJoin(';', layers).Append('\n');
    }
    catch (Exception exception) when (exception is FormatException or InvalidOperationException or System.Xml.XmlException)
    {
        skipped.Add($"{name} ({exception.Message})");
    }
}

File.WriteAllText(Path.Combine(output, "icons.txt"), lines.ToString());
File.Copy(Path.Combine(package, "LICENSE"), Path.Combine(output, "LICENSE"), overwrite: true);
Console.WriteLine($"Wrote {lines.ToString().Count(c => c == '\n')} icons to {output}; skipped {skipped.Count}: {string.Join(", ", skipped)}");
return 0;

/// <summary>
/// Flattens an SVG into fill and stroke layers of path data, each with its composed transform. Covers
/// what the icon set uses: path, circle, ellipse, rect, g (inherited fill, stroke, opacity, transform),
/// use (of an element by id), gradients (drawn with their first stop's color). Defs and clip paths
/// are not drawn themselves.
/// </summary>
internal static class SvgConverter
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    public static (string ViewBox, List<string> Layers) Convert(XDocument document)
    {
        var root = document.Root!;
        var viewBox = (string?)root.Attribute("viewBox") ?? $"0 0 {(string?)root.Attribute("width") ?? "16"} {(string?)root.Attribute("height") ?? "16"}";
        var ids = root.Descendants().Where(e => e.Attribute("id") is not null).GroupBy(e => (string)e.Attribute("id")!).ToDictionary(g => g.Key, g => g.First());
        var layers = new List<string>();
        // The root element can set paint too (fill="none" is common); its children inherit it.
        Walk(root, Apply(root, new Style("#000000", null, 1, 1, "n", "1", "butt", "miter", Identity), ids), ids, layers);
        return (Normalize(viewBox), layers);
    }

    private static readonly double[] Identity = [1, 0, 0, 1, 0, 0];

    private sealed record Style(string? Fill, string? Stroke, double Opacity, double FillOpacity, string FillRule, string StrokeWidth, string Cap, string Join, double[] Matrix);

    private static void Walk(XElement element, Style inherited, Dictionary<string, XElement> ids, List<string> layers)
    {
        foreach (var child in element.Elements())
        {
            var local = child.Name.LocalName;
            if (local is "defs" or "clipPath" or "mask" or "linearGradient" or "radialGradient" or "title" or "desc" or "style" or "metadata")
            {
                continue;
            }

            var style = Apply(child, inherited, ids);
            switch (local)
            {
                case "g":
                    Walk(child, style, ids, layers);
                    break;
                case "svg":
                    Walk(child, style with { Matrix = Multiply(NestedViewBox(child, element), style.Matrix) }, ids, layers);
                    break;
                case "use":
                    var href = ((string?)child.Attribute(XLink + "href") ?? (string?)child.Attribute("href"))?.TrimStart('#');
                    if (href is not null && ids.TryGetValue(href, out var target))
                    {
                        var x = Number(child, "x");
                        var y = Number(child, "y");
                        var placed = style with { Matrix = Multiply([1, 0, 0, 1, x, y], style.Matrix) };
                        var wrapper = new XElement(Svg + "g", new XElement(target));
                        Walk(wrapper, placed, ids, layers);
                    }

                    break;
                default:
                    if (PathData(child) is { Length: > 0 } data)
                    {
                        AddLayers(style, data, layers);
                    }

                    break;
            }
        }
    }

    private static void AddLayers(Style style, string data, List<string> layers)
    {
        var matrix = string.Join(' ', style.Matrix.Select(Format));
        if (style.Fill is { } fill)
        {
            layers.Add($"F|{fill}|{Format(style.Opacity * style.FillOpacity)}|{style.FillRule}|{matrix}|{data}");
        }

        if (style.Stroke is { } stroke)
        {
            layers.Add($"S|{stroke}|{Format(style.Opacity)}|{style.StrokeWidth},{style.Cap},{style.Join}|{matrix}|{data}");
        }
    }

    /// <summary>An element's own presentation attributes (and style="...") over what it inherits.</summary>
    private static Style Apply(XElement element, Style inherited, Dictionary<string, XElement> ids)
    {
        var properties = new Dictionary<string, string>();
        foreach (var attribute in element.Attributes())
        {
            properties[attribute.Name.LocalName] = attribute.Value;
        }

        foreach (var declaration in ((string?)element.Attribute("style") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                properties[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
            }
        }

        var style = inherited with { Opacity = 1 }; // opacity multiplies down the tree, below
        if (properties.TryGetValue("fill", out var fill))
        {
            style = style with { Fill = Paint(fill, ids) };
        }

        if (properties.TryGetValue("stroke", out var stroke))
        {
            style = style with { Stroke = Paint(stroke, ids) };
        }

        style = style with
        {
            Opacity = inherited.Opacity * (properties.TryGetValue("opacity", out var opacity) ? Parse(opacity) : 1),
            FillOpacity = properties.TryGetValue("fill-opacity", out var fillOpacity) ? Parse(fillOpacity) : inherited.FillOpacity,
            FillRule = properties.TryGetValue("fill-rule", out var rule) ? (rule == "evenodd" ? "e" : "n") : inherited.FillRule,
            StrokeWidth = properties.TryGetValue("stroke-width", out var width) ? Format(Parse(width)) : inherited.StrokeWidth,
            Cap = properties.TryGetValue("stroke-linecap", out var cap) ? cap : inherited.Cap,
            Join = properties.TryGetValue("stroke-linejoin", out var join) ? join : inherited.Join,
            Matrix = properties.TryGetValue("transform", out var transform) ? Multiply(Transform(transform), inherited.Matrix) : inherited.Matrix,
        };
        return style;
    }

    /// <summary>A color, null for "none", or a gradient's first stop color.</summary>
    private static string? Paint(string value, Dictionary<string, XElement> ids)
    {
        value = value.Trim();
        if (value == "none")
        {
            return null;
        }

        if (value.StartsWith("url(#", StringComparison.Ordinal))
        {
            var id = value[5..value.IndexOf(')', StringComparison.Ordinal)];
            var gradient = ids.GetValueOrDefault(id);
            while (gradient is not null && !gradient.Elements().Any(e => e.Name.LocalName == "stop")
                   && ((string?)gradient.Attribute(XLink + "href") ?? (string?)gradient.Attribute("href"))?.TrimStart('#') is { } inheritedId)
            {
                gradient = ids.GetValueOrDefault(inheritedId); // a gradient that borrows another's stops
            }

            var stop = gradient?.Elements().FirstOrDefault(e => e.Name.LocalName == "stop");
            var color = (string?)stop?.Attribute("stop-color")
                        ?? ((string?)stop?.Attribute("style"))?.Split(';').Select(s => s.Split(':')).FirstOrDefault(p => p[0].Trim() == "stop-color")?[1].Trim();
            return color ?? "#000000";
        }

        return value;
    }

    /// <summary>Path data for any drawable shape.</summary>
    private static string? PathData(XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "path":
                return ((string?)element.Attribute("d"))?.Trim();
            case "circle":
                return Ellipse(Number(element, "cx"), Number(element, "cy"), Number(element, "r"), Number(element, "r"));
            case "ellipse":
                return Ellipse(Number(element, "cx"), Number(element, "cy"), Number(element, "rx"), Number(element, "ry"));
            case "rect":
                double x = Number(element, "x"), y = Number(element, "y"), w = Number(element, "width"), h = Number(element, "height");
                var rx = element.Attribute("rx") is not null ? Number(element, "rx") : Number(element, "ry");
                var ry = element.Attribute("ry") is not null ? Number(element, "ry") : rx;
                rx = Math.Min(rx, w / 2);
                ry = Math.Min(ry, h / 2);
                return rx <= 0
                    ? $"M{Format(x)} {Format(y)}h{Format(w)}v{Format(h)}h{Format(-w)}Z"
                    : $"M{Format(x + rx)} {Format(y)}h{Format(w - 2 * rx)}a{Format(rx)} {Format(ry)} 0 0 1 {Format(rx)} {Format(ry)}"
                      + $"v{Format(h - 2 * ry)}a{Format(rx)} {Format(ry)} 0 0 1 {Format(-rx)} {Format(ry)}h{Format(-(w - 2 * rx))}"
                      + $"a{Format(rx)} {Format(ry)} 0 0 1 {Format(-rx)} {Format(-ry)}v{Format(-(h - 2 * ry))}a{Format(rx)} {Format(ry)} 0 0 1 {Format(rx)} {Format(-ry)}Z";
            case "polygon" or "polyline":
                var points = ((string?)element.Attribute("points") ?? string.Empty).Trim();
                return points.Length == 0 ? null : "M" + points + (element.Name.LocalName == "polygon" ? "Z" : string.Empty);
            default:
                return null;
        }
    }

    private static string Ellipse(double cx, double cy, double rx, double ry) =>
        $"M{Format(cx - rx)} {Format(cy)}a{Format(rx)} {Format(ry)} 0 1 0 {Format(2 * rx)} 0a{Format(rx)} {Format(ry)} 0 1 0 {Format(-2 * rx)} 0Z";

    /// <summary>"translate(..) scale(..) rotate(..) matrix(..)" composed into one matrix [a b c d e f].</summary>
    private static double[] Transform(string value)
    {
        var result = Identity;
        foreach (var part in value.Split(')', StringSplitOptions.RemoveEmptyEntries))
        {
            var open = part.IndexOf('(', StringComparison.Ordinal);
            if (open < 0)
            {
                continue;
            }

            var name = part[..open].Trim().TrimStart(',').Trim();
            var n = part[(open + 1)..].Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToArray();
            double[] m = name switch
            {
                "translate" => [1, 0, 0, 1, n[0], n.Length > 1 ? n[1] : 0],
                "scale" => [n[0], 0, 0, n.Length > 1 ? n[1] : n[0], 0, 0],
                "matrix" => n,
                "rotate" => Rotate(n),
                _ => Identity,
            };
            result = Multiply(m, result); // later transforms apply first (closest to the shape)
        }

        return result;
    }

    /// <summary>
    /// A nested &lt;svg&gt; fills its parent's view box: its own view box is scaled to fit and centered
    /// (SVG's default "xMidYMid meet").
    /// </summary>
    private static double[] NestedViewBox(XElement nested, XElement parent)
    {
        double[] Box(XElement e) => ((string?)e.Attribute("viewBox") ?? "0 0 16 16").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToArray();
        var inner = Box(nested);
        var outer = Box(parent);
        var scale = Math.Min(outer[2] / inner[2], outer[3] / inner[3]);
        var dx = outer[0] + (outer[2] - inner[2] * scale) / 2 - inner[0] * scale;
        var dy = outer[1] + (outer[3] - inner[3] * scale) / 2 - inner[1] * scale;
        return [scale, 0, 0, scale, dx, dy];
    }

    private static double[] Rotate(double[] n)
    {
        var angle = n[0] * Math.PI / 180;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        double[] rotation = [cos, sin, -sin, cos, 0, 0];
        if (n.Length < 3)
        {
            return rotation;
        }

        // rotate(a, cx, cy) = translate(cx, cy) rotate(a) translate(-cx, -cy)
        return Multiply(Multiply([1, 0, 0, 1, -n[1], -n[2]], rotation), [1, 0, 0, 1, n[1], n[2]]);
    }

    /// <summary>Applies <paramref name="first"/>, then <paramref name="then"/> (row-vector convention, like Avalonia's Matrix).</summary>
    private static double[] Multiply(double[] first, double[] then) =>
    [
        first[0] * then[0] + first[1] * then[2],
        first[0] * then[1] + first[1] * then[3],
        first[2] * then[0] + first[3] * then[2],
        first[2] * then[1] + first[3] * then[3],
        first[4] * then[0] + first[5] * then[2] + then[4],
        first[4] * then[1] + first[5] * then[3] + then[5],
    ];

    private static double Number(XElement element, string name) =>
        element.Attribute(name) is { } attribute ? Parse(attribute.Value) : 0;

    private static double Parse(string value) =>
        double.Parse(value.Trim().TrimEnd('%').Replace("px", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string Format(double value) => Math.Round(value, 4).ToString(CultureInfo.InvariantCulture);

    private static string Normalize(string viewBox) =>
        string.Join(' ', viewBox.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(v => Format(Parse(v))));
}
