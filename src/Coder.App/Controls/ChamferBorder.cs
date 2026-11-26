using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Coder.App.Controls;

/// <summary>Which corners of a <see cref="ChamferBorder"/> are cut.</summary>
[Flags]
public enum ChamferCorners
{
    None = 0,
    TopLeft = 1,
    TopRight = 2,
    BottomRight = 4,
    BottomLeft = 8,

    /// <summary>Top-left and bottom-right: the brand's default, echoing the ends of the logo's "C".</summary>
    Diagonal = TopLeft | BottomRight,
    All = TopLeft | TopRight | BottomRight | BottomLeft,
}

/// <summary>
/// A border whose corners are cut at 45° instead of rounded: the angular shape of the Coder logo.
/// Draws a background and an optional outline, then hosts one child like <see cref="Decorator"/>.
/// </summary>
public sealed class ChamferBorder : Decorator
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<ChamferBorder>();

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        Border.BorderBrushProperty.AddOwner<ChamferBorder>();

    public static readonly StyledProperty<double> BorderThicknessProperty =
        AvaloniaProperty.Register<ChamferBorder, double>(nameof(BorderThickness));

    /// <summary>Length of each cut, measured along the edge.</summary>
    public static readonly StyledProperty<double> ChamferProperty =
        AvaloniaProperty.Register<ChamferBorder, double>(nameof(Chamfer));

    public static readonly StyledProperty<ChamferCorners> CornersProperty =
        AvaloniaProperty.Register<ChamferBorder, ChamferCorners>(nameof(Corners), ChamferCorners.Diagonal);

    /// <summary>
    /// Optional accent stroke along the top edge that bends down along the top cut corners
    /// (e.g. the active tab's highlight). It is what makes a cut visible on low-contrast surfaces.
    /// </summary>
    public static readonly StyledProperty<IBrush?> TopEdgeBrushProperty =
        AvaloniaProperty.Register<ChamferBorder, IBrush?>(nameof(TopEdgeBrush));

    private const double TopEdgeThickness = 2;

    static ChamferBorder()
    {
        AffectsRender<ChamferBorder>(BackgroundProperty, BorderBrushProperty, BorderThicknessProperty, ChamferProperty, CornersProperty, TopEdgeBrushProperty);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public double BorderThickness
    {
        get => GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public double Chamfer
    {
        get => GetValue(ChamferProperty);
        set => SetValue(ChamferProperty, value);
    }

    public ChamferCorners Corners
    {
        get => GetValue(CornersProperty);
        set => SetValue(CornersProperty, value);
    }

    public IBrush? TopEdgeBrush
    {
        get => GetValue(TopEdgeBrushProperty);
        set => SetValue(TopEdgeBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var hasOutline = BorderBrush is not null && BorderThickness > 0;
        if (Background is not null || hasOutline)
        {
            // Inset by half the stroke so the outline stays inside the control's bounds.
            var inset = hasOutline ? BorderThickness / 2 : 0;
            var shape = CreateShape(new Rect(Bounds.Size).Deflate(inset), Chamfer, Corners);
            context.DrawGeometry(Background, hasOutline ? new Pen(BorderBrush, BorderThickness) : null, shape);
        }

        if (TopEdgeBrush is not null)
        {
            var pen = new Pen(TopEdgeBrush, TopEdgeThickness, lineCap: PenLineCap.Flat, lineJoin: PenLineJoin.Miter);
            context.DrawGeometry(null, pen, CreateTopEdge(new Rect(Bounds.Size).Deflate(TopEdgeThickness / 2), Chamfer, Corners));
        }
    }

    /// <summary>The top edge of the shape, including the diagonal of any cut top corner.</summary>
    private static Geometry CreateTopEdge(Rect rect, double chamfer, ChamferCorners corners)
    {
        var size = Math.Max(0, Math.Min(chamfer, Math.Min(rect.Width, rect.Height) / 2));
        var left = corners.HasFlag(ChamferCorners.TopLeft) ? size : 0;
        var right = corners.HasFlag(ChamferCorners.TopRight) ? size : 0;

        var geometry = new StreamGeometry();
        using var figure = geometry.Open();
        figure.BeginFigure(new Point(rect.Left, rect.Top + left), isFilled: false);
        figure.LineTo(new Point(rect.Left + left, rect.Top));
        figure.LineTo(new Point(rect.Right - right, rect.Top));
        figure.LineTo(new Point(rect.Right, rect.Top + right));
        figure.EndFigure(isClosed: false);
        return geometry;
    }

    /// <summary>The outline of a rectangle with the given corners cut off.</summary>
    public static Geometry CreateShape(Rect rect, double chamfer, ChamferCorners corners)
    {
        var size = Math.Max(0, Math.Min(chamfer, Math.Min(rect.Width, rect.Height) / 2));
        double Cut(ChamferCorners corner) => corners.HasFlag(corner) ? size : 0;

        var geometry = new StreamGeometry();
        using var figure = geometry.Open();
        figure.BeginFigure(new Point(rect.Left + Cut(ChamferCorners.TopLeft), rect.Top), isFilled: true);
        figure.LineTo(new Point(rect.Right - Cut(ChamferCorners.TopRight), rect.Top));
        figure.LineTo(new Point(rect.Right, rect.Top + Cut(ChamferCorners.TopRight)));
        figure.LineTo(new Point(rect.Right, rect.Bottom - Cut(ChamferCorners.BottomRight)));
        figure.LineTo(new Point(rect.Right - Cut(ChamferCorners.BottomRight), rect.Bottom));
        figure.LineTo(new Point(rect.Left + Cut(ChamferCorners.BottomLeft), rect.Bottom));
        figure.LineTo(new Point(rect.Left, rect.Bottom - Cut(ChamferCorners.BottomLeft)));
        figure.LineTo(new Point(rect.Left, rect.Top + Cut(ChamferCorners.TopLeft)));
        figure.EndFigure(isClosed: true);
        return geometry;
    }
}
