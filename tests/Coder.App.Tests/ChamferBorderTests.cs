using Avalonia;
using Avalonia.Headless.XUnit;
using Coder.App.Controls;

namespace Coder.App.Tests;

public sealed class ChamferBorderTests
{
    [AvaloniaFact]
    public void Diagonal_shape_cuts_only_the_top_left_and_bottom_right_corners()
    {
        var shape = ChamferBorder.CreateShape(new Rect(0, 0, 100, 40), chamfer: 10, ChamferCorners.Diagonal);

        Assert.False(shape.FillContains(new Point(1, 1)));     // top-left cut
        Assert.False(shape.FillContains(new Point(99, 39)));   // bottom-right cut
        Assert.True(shape.FillContains(new Point(99, 1)));     // top-right kept
        Assert.True(shape.FillContains(new Point(1, 39)));     // bottom-left kept
        Assert.True(shape.FillContains(new Point(50, 20)));
    }

    [AvaloniaFact]
    public void Chamfer_never_exceeds_half_the_shortest_side()
    {
        var shape = ChamferBorder.CreateShape(new Rect(0, 0, 100, 10), chamfer: 50, ChamferCorners.All);

        // Capped at 5: the middle of the shape is still filled edge to edge.
        Assert.True(shape.FillContains(new Point(50, 1)));
        Assert.True(shape.FillContains(new Point(50, 9)));
    }
}
