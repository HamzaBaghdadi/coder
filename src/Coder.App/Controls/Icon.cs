using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Coder.App.Controls;

/// <summary>
/// Draws an outline icon defined on a 24×24 grid, scaled to the control's size like an SVG.
/// The stroke uses the inherited <see cref="TextElement.ForegroundProperty"/>, so an icon inside a
/// button automatically follows the button's hover/active text color.
/// </summary>
public sealed class Icon : Control
{
    private const double GridSize = 24;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.75);

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, ForegroundProperty, StrokeThicknessProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<Icon>(false);
    }

    public Icon()
    {
        Width = Height = 16;
    }

    public Icon(Geometry data, double size = 16)
    {
        Data = data;
        Width = Height = size;
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is null || Foreground is null)
        {
            return;
        }

        var pen = new Pen(Foreground, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(Bounds.Width / GridSize, Bounds.Height / GridSize)))
        {
            context.DrawGeometry(null, pen, Data);
        }
    }
}
