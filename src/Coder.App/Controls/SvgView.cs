using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Coder.App.Theme;
using Coder.Core.Svg;

namespace Coder.App.Controls;

/// <summary>
/// Draws a parsed <see cref="SvgDocument"/> centered in its bounds, scaled uniformly (aspect kept) to fit.
/// <see cref="Zoom"/> 0 fits the bounds at any scale; a positive <see cref="Zoom"/> caps the scale at that value, and
/// the control then asks for the document size times <see cref="Zoom"/>, so inside a ScrollViewer (unbounded space)
/// it draws at exactly that zoom and scrolls. Geometries, brushes and text are built once per document; an element
/// whose path data cannot be read is skipped and the rest still draws.
/// </summary>
public sealed class SvgView : Control
{
    public static readonly StyledProperty<SvgDocument?> DocumentProperty =
        AvaloniaProperty.Register<SvgView, SvgDocument?>(nameof(Document));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<SvgView, double>(nameof(Zoom), 0, coerce: (_, value) => double.IsFinite(value) && value > 0 ? Math.Min(value, 256) : 0);

    public static readonly StyledProperty<bool> ShowTransparencyGridProperty =
        AvaloniaProperty.Register<SvgView, bool>(nameof(ShowTransparencyGrid));

    private const double GridCell = 8;

    private RenderNode[]? _nodes;
    private double _rootOpacity = 1;
    private Geometry? _gridGeometry;
    private Size _gridSize;

    static SvgView()
    {
        AffectsRender<SvgView>(DocumentProperty, ZoomProperty, ShowTransparencyGridProperty);
        AffectsMeasure<SvgView>(DocumentProperty, ZoomProperty);
    }

    public SvgView()
    {
        ClipToBounds = true;
    }

    /// <summary>The image to draw; null draws nothing.</summary>
    public SvgDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>0 (default) fits the bounds; a positive value is the largest scale used (1 = actual size).</summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>Draws a checkerboard behind the image so transparent areas are visible.</summary>
    public bool ShowTransparencyGrid
    {
        get => GetValue(ShowTransparencyGridProperty);
        set => SetValue(ShowTransparencyGridProperty, value);
    }

    /// <summary>How many shapes or texts of the current document could not be drawn (for example bad path data).</summary>
    public int SkippedItemCount { get; private set; }

    /// <summary>The scale <see cref="Render"/> uses for a document of the given size in the given bounds.</summary>
    public static double ScaleFor(double documentWidth, double documentHeight, double boundsWidth, double boundsHeight, double zoom)
    {
        if (!(documentWidth > 0) || !(documentHeight > 0) || !(boundsWidth > 0) || !(boundsHeight > 0))
        {
            return 0;
        }

        var fit = Math.Min(boundsWidth / documentWidth, boundsHeight / documentHeight);
        return zoom > 0 ? Math.Min(fit, zoom) : fit;
    }

    /// <summary>The rectangle (in this control's coordinates) the image occupies.</summary>
    public Rect ImageRect
    {
        get
        {
            if (Document is not { } document)
            {
                return default;
            }

            var scale = ScaleFor(document.Width, document.Height, Bounds.Width, Bounds.Height, Zoom);
            var width = document.Width * scale;
            var height = document.Height * scale;
            return new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
        {
            _nodes = null;
            SkippedItemCount = 0;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Document is not { } document)
        {
            return default;
        }

        var zoom = Zoom > 0 ? Zoom : 1;
        var width = document.Width * zoom;
        var height = document.Height * zoom;
        return new Size(
            double.IsFinite(availableSize.Width) ? Math.Min(width, availableSize.Width) : width,
            double.IsFinite(availableSize.Height) ? Math.Min(height, availableSize.Height) : height);
    }

    public override void Render(DrawingContext context)
    {
        if (Document is not { } document)
        {
            return;
        }

        var rect = ImageRect;
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        if (ShowTransparencyGrid)
        {
            DrawGrid(context, rect);
        }

        _nodes ??= Build(document);
        var scale = rect.Width / document.Width;
        using (context.PushClip(rect))
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(rect.X, rect.Y)))
        using (context.PushOpacity(_rootOpacity))
        {
            foreach (var node in _nodes)
            {
                node.Draw(context);
            }
        }
    }

    private void DrawGrid(DrawingContext context, Rect rect)
    {
        if (_gridGeometry is null || _gridSize != rect.Size)
        {
            var geometry = new StreamGeometry();
            using (var sink = geometry.Open())
            {
                var columns = (int)Math.Ceiling(rect.Width / GridCell);
                var rows = (int)Math.Ceiling(rect.Height / GridCell);
                for (var row = 0; row < rows; row++)
                {
                    for (var column = row % 2; column < columns; column += 2)
                    {
                        var x = column * GridCell;
                        var y = row * GridCell;
                        sink.BeginFigure(new Point(x, y), true);
                        sink.LineTo(new Point(x + GridCell, y));
                        sink.LineTo(new Point(x + GridCell, y + GridCell));
                        sink.LineTo(new Point(x, y + GridCell));
                        sink.EndFigure(true);
                    }
                }
            }

            _gridGeometry = geometry;
            _gridSize = rect.Size;
        }

        using (context.PushClip(rect))
        {
            context.DrawRectangle(Palette.EditorBackground, null, rect);
            using (context.PushTransform(Matrix.CreateTranslation(rect.X, rect.Y)))
            {
                context.DrawGeometry(Palette.Elevated, null, _gridGeometry);
            }
        }
    }

    // ---- Building the render cache ----

    private RenderNode[] Build(SvgDocument document)
    {
        SkippedItemCount = 0;
        _rootOpacity = document.Root.Opacity;
        return BuildAll(document.Root.Children);
    }

    private RenderNode[] BuildAll(IReadOnlyList<SvgItem> items)
    {
        var nodes = new List<RenderNode>(items.Count);
        foreach (var item in items)
        {
            RenderNode? node = null;
            try
            {
                node = item switch
                {
                    SvgGroup group => new GroupNode(group.Opacity, BuildAll(group.Children)),
                    SvgShape shape => BuildShape(shape),
                    SvgText text => BuildText(text),
                    _ => null,
                };
            }
            catch (Exception)
            {
                node = null;
            }

            if (node is null)
            {
                SkippedItemCount += item is SvgGroup ? 0 : 1;
                continue;
            }

            nodes.Add(node);
        }

        return [.. nodes];
    }

    private static Matrix ToMatrix(SvgMatrix m) => new(m.A, m.B, m.C, m.D, m.E, m.F);

    private static ShapeNode? BuildShape(SvgShape shape)
    {
        var data = SvgPathData.Normalize(shape.PathData);
        if (data.Length == 0)
        {
            return null;
        }

        Geometry geometry;
        try
        {
            // Avalonia follows WPF: F0 is even-odd, F1 nonzero (StreamGeometry otherwise defaults to even-odd).
            geometry = Geometry.Parse((shape.FillRule == SvgFillRule.EvenOdd ? "F0 " : "F1 ") + data);
        }
        catch (Exception)
        {
            return null;
        }

        var bounds = geometry.Bounds;
        var fill = CreateBrush(shape.Fill, shape.FillOpacity, bounds);
        IPen? pen = null;
        if (shape.StrokeWidth > 0 && CreateBrush(shape.Stroke, shape.StrokeOpacity, bounds) is { } strokeBrush)
        {
            var width = shape.StrokeWidth;
            IDashStyle? dashes = shape.StrokeDashArray.Count > 0
                ? new ImmutableDashStyle(shape.StrokeDashArray.Select(d => d / width), shape.StrokeDashOffset / width)
                : null;
            pen = new Pen(strokeBrush, width, dashes,
                shape.StrokeLineCap switch { SvgLineCap.Round => PenLineCap.Round, SvgLineCap.Square => PenLineCap.Square, _ => PenLineCap.Flat },
                shape.StrokeLineJoin switch { SvgLineJoin.Round => PenLineJoin.Round, SvgLineJoin.Bevel => PenLineJoin.Bevel, _ => PenLineJoin.Miter },
                shape.StrokeMiterLimit);
        }

        return fill is null && pen is null ? null : new ShapeNode(ToMatrix(shape.Transform), shape.Opacity, geometry, fill, pen);
    }

    private static TextNode? BuildText(SvgText text)
    {
        var placed = new List<(FormattedText Text, double X, double Baseline, int Chunk, SvgTextRun Run)>();
        var anchors = new List<SvgTextAnchor>();
        var x = text.X;
        var y = text.Y;
        foreach (var run in text.Runs)
        {
            if (run.X is not null || anchors.Count == 0)
            {
                x = run.X ?? x;
                anchors.Add(run.TextAnchor);
            }

            y = run.Y ?? y;
            x += run.Dx;
            y += run.Dy;
            var size = Math.Min(run.FontSize, 4096);
            if (!(size > 0))
            {
                continue;
            }

            var typeface = new Typeface(FontFamilyFor(run.FontFamily), run.Italic ? FontStyle.Italic : FontStyle.Normal,
                (FontWeight)Math.Clamp(run.FontWeight, 1, 999), FontStretch.Normal);
            var formatted = new FormattedText(run.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, null);
            placed.Add((formatted, x, y, anchors.Count - 1, run));
            x += formatted.WidthIncludingTrailingWhitespace;
        }

        if (placed.Count == 0)
        {
            return null;
        }

        // text-anchor shifts each chunk (a run of runs started by an absolute x) by part of its width.
        var shifts = new double[anchors.Count];
        for (var chunk = 0; chunk < anchors.Count; chunk++)
        {
            var members = placed.Where(p => p.Chunk == chunk).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            var width = (members[^1].X + members[^1].Text.WidthIncludingTrailingWhitespace) - members[0].X;
            shifts[chunk] = anchors[chunk] switch { SvgTextAnchor.Middle => -width / 2, SvgTextAnchor.End => -width, _ => 0 };
        }

        var glyphs = new List<(FormattedText, Point)>();
        var boxes = placed.Select(p => new Rect(p.X + shifts[p.Chunk], p.Baseline - p.Text.Baseline, p.Text.WidthIncludingTrailingWhitespace, p.Text.Height)).ToList();
        var union = boxes.Aggregate((a, b) => a.Union(b));
        for (var i = 0; i < placed.Count; i++)
        {
            var (formatted, _, _, _, run) = placed[i];
            if (CreateBrush(run.Fill, run.FillOpacity, union) is not { } brush)
            {
                continue;
            }

            formatted.SetForegroundBrush(brush);
            glyphs.Add((formatted, boxes[i].TopLeft));
        }

        return glyphs.Count == 0 ? null : new TextNode(ToMatrix(text.Transform), text.Opacity, [.. glyphs]);
    }

    /// <summary>Maps CSS generic families onto the app's fonts; named families pass through as a fallback list.</summary>
    private static FontFamily FontFamilyFor(string families)
    {
        var names = families.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(name => name.ToLowerInvariant() switch
            {
                "sans-serif" or "system-ui" or "ui-sans-serif" => Typography.Ui.FamilyNames,
                "monospace" or "ui-monospace" => Typography.Code.FamilyNames,
                "serif" or "ui-serif" => ["Georgia", "Times New Roman", "DejaVu Serif"],
                _ => (IEnumerable<string>)[name],
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 0 ? Typography.Ui : new FontFamily(string.Join(", ", names));
    }

    /// <summary>A brush for a paint, or null when nothing should be painted. <paramref name="bounds"/> is the object
    /// bounding box that objectBoundingBox gradients are relative to.</summary>
    private static IBrush? CreateBrush(SvgPaint paint, double opacity, Rect bounds)
    {
        if (opacity <= 0)
        {
            return null;
        }

        switch (paint.Kind)
        {
            case SvgPaintKind.Color:
                var c = paint.Color;
                return c.A == 0 ? null : new ImmutableSolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B), opacity);
            case SvgPaintKind.Gradient when paint.Gradient is { } gradient:
                var transform = ToMatrix(gradient.Transform);
                if (gradient.Units == SvgGradientUnits.ObjectBoundingBox)
                {
                    if (bounds.Width <= 0 || bounds.Height <= 0)
                    {
                        return null; // The spec does not paint a bounding-box gradient on a zero-area box.
                    }

                    transform *= new Matrix(bounds.Width, 0, 0, bounds.Height, bounds.X, bounds.Y);
                }

                var stops = new GradientStops();
                foreach (var stop in gradient.Stops)
                {
                    stops.Add(new GradientStop(Color.FromArgb(stop.Color.A, stop.Color.R, stop.Color.G, stop.Color.B), stop.Offset));
                }

                var spread = gradient.SpreadMethod switch
                {
                    SvgSpreadMethod.Reflect => GradientSpreadMethod.Reflect,
                    SvgSpreadMethod.Repeat => GradientSpreadMethod.Repeat,
                    _ => GradientSpreadMethod.Pad,
                };
                var origin = new RelativePoint(0, 0, RelativeUnit.Absolute);
                GradientBrush brush = gradient switch
                {
                    SvgLinearGradient linear => new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(linear.X1, linear.Y1, RelativeUnit.Absolute),
                        EndPoint = new RelativePoint(linear.X2, linear.Y2, RelativeUnit.Absolute),
                    },
                    SvgRadialGradient radial => new RadialGradientBrush
                    {
                        Center = new RelativePoint(radial.Cx, radial.Cy, RelativeUnit.Absolute),
                        GradientOrigin = new RelativePoint(radial.Fx, radial.Fy, RelativeUnit.Absolute),
                        RadiusX = new RelativeScalar(radial.R, RelativeUnit.Absolute),
                        RadiusY = new RelativeScalar(radial.R, RelativeUnit.Absolute),
                    },
                    _ => throw new InvalidOperationException("Unknown gradient."),
                };
                brush.GradientStops = stops;
                brush.SpreadMethod = spread;
                brush.Opacity = opacity;
                brush.Transform = new MatrixTransform(transform);
                brush.TransformOrigin = origin;
                return brush.ToImmutable();
            default:
                return null;
        }
    }

    // ---- Render nodes ----

    private abstract class RenderNode
    {
        public abstract void Draw(DrawingContext context);
    }

    private sealed class GroupNode(double opacity, RenderNode[] children) : RenderNode
    {
        public override void Draw(DrawingContext context)
        {
            using (context.PushOpacity(opacity))
            {
                foreach (var child in children)
                {
                    child.Draw(context);
                }
            }
        }
    }

    private sealed class ShapeNode(Matrix transform, double opacity, Geometry geometry, IBrush? fill, IPen? pen) : RenderNode
    {
        public override void Draw(DrawingContext context)
        {
            using (context.PushTransform(transform))
            using (context.PushOpacity(opacity))
            {
                context.DrawGeometry(fill, pen, geometry);
            }
        }
    }

    private sealed class TextNode(Matrix transform, double opacity, (FormattedText Text, Point Origin)[] runs) : RenderNode
    {
        public override void Draw(DrawingContext context)
        {
            using (context.PushTransform(transform))
            using (context.PushOpacity(opacity))
            {
                foreach (var (text, origin) in runs)
                {
                    context.DrawText(text, origin);
                }
            }
        }
    }
}
