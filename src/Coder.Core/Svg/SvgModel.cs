namespace Coder.Core.Svg;

/// <summary>Thrown by <see cref="SvgDocument.Parse"/> when the input is not XML or not an SVG document.</summary>
public sealed class SvgException : Exception
{
    public SvgException()
    {
    }

    public SvgException(string message) : base(message)
    {
    }

    public SvgException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>A straight (non-premultiplied) sRGB color with alpha.</summary>
public readonly record struct SvgColor(byte R, byte G, byte B, byte A = 255)
{
    public static readonly SvgColor Black = new(0, 0, 0);
    public static readonly SvgColor Transparent = new(0, 0, 0, 0);

    /// <summary>"#RRGGBBAA", for debugging and tests.</summary>
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}

/// <summary>
/// A 2D affine transform with SVG's <c>matrix(a b c d e f)</c> layout: x' = a·x + c·y + e, y' = b·x + d·y + f.
/// The fields map one to one onto Avalonia's <c>Matrix(m11, m12, m21, m22, m31, m32)</c>.
/// </summary>
public readonly record struct SvgMatrix(double A, double B, double C, double D, double E, double F)
{
    public static readonly SvgMatrix Identity = new(1, 0, 0, 1, 0, 0);

    public bool IsIdentity => this == Identity;

    public static SvgMatrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    public static SvgMatrix Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    /// <summary>Rotation by <paramref name="degrees"/> (clockwise on screen, as in SVG).</summary>
    public static SvgMatrix Rotate(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new(cos, sin, -sin, cos, 0, 0);
    }

    public static SvgMatrix SkewX(double degrees) => new(1, 0, Math.Tan(degrees * Math.PI / 180), 1, 0, 0);

    public static SvgMatrix SkewY(double degrees) => new(1, Math.Tan(degrees * Math.PI / 180), 0, 1, 0, 0);

    /// <summary>The transform that applies this one first, then <paramref name="next"/>.</summary>
    public SvgMatrix Then(SvgMatrix next) => new(
        (next.A * A) + (next.C * B),
        (next.B * A) + (next.D * B),
        (next.A * C) + (next.C * D),
        (next.B * C) + (next.D * D),
        (next.A * E) + (next.C * F) + next.E,
        (next.B * E) + (next.D * F) + next.F);

    public (double X, double Y) Apply(double x, double y) => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);
}

/// <summary>The <c>viewBox</c> rectangle in user units.</summary>
public readonly record struct SvgViewBox(double X, double Y, double Width, double Height);

public enum SvgPaintKind
{
    None,
    Color,
    Gradient,
}

/// <summary>A fill or stroke paint. <c>currentColor</c> is already resolved to a color.</summary>
public sealed record SvgPaint(SvgPaintKind Kind, SvgColor Color, SvgGradient? Gradient)
{
    public static readonly SvgPaint None = new(SvgPaintKind.None, SvgColor.Transparent, null);

    public static SvgPaint Solid(SvgColor color) => new(SvgPaintKind.Color, color, null);

    public static SvgPaint From(SvgGradient gradient) => new(SvgPaintKind.Gradient, SvgColor.Transparent, gradient);

    public bool IsNone => Kind == SvgPaintKind.None;
}

public enum SvgGradientUnits
{
    ObjectBoundingBox,
    UserSpaceOnUse,
}

public enum SvgSpreadMethod
{
    Pad,
    Reflect,
    Repeat,
}

/// <summary>A gradient stop; <c>stop-opacity</c> is folded into the color's alpha.</summary>
public readonly record struct SvgGradientStop(double Offset, SvgColor Color);

/// <summary>
/// A linear or radial gradient with its <c>href</c> chain already merged. Coordinates are fractions of the
/// shape's bounding box for <see cref="SvgGradientUnits.ObjectBoundingBox"/>, user units otherwise (percentages
/// resolved against the viewport). <see cref="Transform"/> maps gradient space into that space.
/// </summary>
public abstract record SvgGradient(
    string Id,
    IReadOnlyList<SvgGradientStop> Stops,
    SvgGradientUnits Units,
    SvgMatrix Transform,
    SvgSpreadMethod SpreadMethod);

public sealed record SvgLinearGradient(
    string Id, IReadOnlyList<SvgGradientStop> Stops, SvgGradientUnits Units, SvgMatrix Transform, SvgSpreadMethod SpreadMethod,
    double X1, double Y1, double X2, double Y2)
    : SvgGradient(Id, Stops, Units, Transform, SpreadMethod);

public sealed record SvgRadialGradient(
    string Id, IReadOnlyList<SvgGradientStop> Stops, SvgGradientUnits Units, SvgMatrix Transform, SvgSpreadMethod SpreadMethod,
    double Cx, double Cy, double R, double Fx, double Fy)
    : SvgGradient(Id, Stops, Units, Transform, SpreadMethod);

public enum SvgFillRule
{
    NonZero,
    EvenOdd,
}

public enum SvgLineCap
{
    Butt,
    Round,
    Square,
}

public enum SvgLineJoin
{
    Miter,
    Round,
    Bevel,
}

public enum SvgTextAnchor
{
    Start,
    Middle,
    End,
}

/// <summary>A node of the render tree.</summary>
public abstract class SvgItem
{
    /// <summary>The element's <c>id</c>, if any.</summary>
    public string? Id { get; init; }

    /// <summary>The element name it came from (<c>rect</c>, <c>path</c>, <c>text</c>, <c>g</c>...).</summary>
    public string ElementName { get; init; } = "";

    /// <summary>The accumulated transform: maps this item's user space into document pixels (0..Width, 0..Height).</summary>
    public SvgMatrix Transform { get; init; } = SvgMatrix.Identity;

    /// <summary>The element's own <c>opacity</c> (not multiplied by ancestors'; groups carry theirs).</summary>
    public double Opacity { get; init; } = 1;
}

/// <summary>
/// A group with <c>opacity</c> below 1, which must be composited as a whole. Groups at full opacity are flattened
/// into their parent, since transforms and styles are already accumulated on every leaf.
/// </summary>
public sealed class SvgGroup : SvgItem
{
    public IReadOnlyList<SvgItem> Children { get; init; } = [];
}

/// <summary>A filled and/or stroked shape; every basic shape is converted to path data.</summary>
public sealed class SvgShape : SvgItem
{
    /// <summary>SVG path data: a <c>path</c>'s <c>d</c> as written, or generated for basic shapes.</summary>
    public string PathData { get; init; } = "";

    public SvgPaint Fill { get; init; } = SvgPaint.Solid(SvgColor.Black);

    public SvgPaint Stroke { get; init; } = SvgPaint.None;

    public double FillOpacity { get; init; } = 1;

    public double StrokeOpacity { get; init; } = 1;

    public double StrokeWidth { get; init; } = 1;

    public SvgLineCap StrokeLineCap { get; init; }

    public SvgLineJoin StrokeLineJoin { get; init; }

    public double StrokeMiterLimit { get; init; } = 4;

    /// <summary>Dash lengths in user units (already repeated to an even count); empty for a solid line.</summary>
    public IReadOnlyList<double> StrokeDashArray { get; init; } = [];

    public double StrokeDashOffset { get; init; }

    public SvgFillRule FillRule { get; init; }
}

/// <summary>A run of characters within a text element (the text's own characters or a <c>tspan</c>).</summary>
public sealed class SvgTextRun
{
    public string Text { get; init; } = "";

    /// <summary>Absolute position that starts a new chunk; null continues after the previous run.</summary>
    public double? X { get; init; }

    public double? Y { get; init; }

    public double Dx { get; init; }

    public double Dy { get; init; }

    public double FontSize { get; init; } = 16;

    /// <summary>The CSS family list with quotes removed, e.g. <c>Segoe UI, sans-serif</c>.</summary>
    public string FontFamily { get; init; } = "sans-serif";

    /// <summary>100..900; normal is 400, bold 700.</summary>
    public int FontWeight { get; init; } = 400;

    public bool Italic { get; init; }

    public SvgTextAnchor TextAnchor { get; init; }

    public SvgPaint Fill { get; init; } = SvgPaint.Solid(SvgColor.Black);

    public double FillOpacity { get; init; } = 1;
}

/// <summary>
/// A <c>text</c> element: runs laid out left to right from (<see cref="X"/>, <see cref="Y"/>) on the baseline.
/// Layout (measuring glyphs) is left to the renderer.
/// </summary>
public sealed class SvgText : SvgItem
{
    public double X { get; init; }

    public double Y { get; init; }

    public IReadOnlyList<SvgTextRun> Runs { get; init; } = [];

    /// <summary>All runs' characters, for tests and accessibility.</summary>
    public string Content => string.Concat(Runs.Select(r => r.Text));
}
