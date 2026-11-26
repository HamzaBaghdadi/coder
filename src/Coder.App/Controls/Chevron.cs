using System.Linq.Expressions;
using Avalonia;
using Avalonia.Media;
using Coder.App.Mvvm;
using Coder.App.Theme;

namespace Coder.App.Controls;

/// <summary>The expand/collapse arrow: points right when collapsed, down when expanded.</summary>
public static class Chevron
{
    private static readonly ITransform Collapsed = new RotateTransform(0);
    private static readonly ITransform Expanded = new RotateTransform(90);

    /// <summary>
    /// Binds the arrow to a flag. A binding (rather than an event handler) is released when the row
    /// leaves the visual tree, so recycled list rows never leak.
    /// </summary>
    public static Icon For<TSource>(TSource source, Expression<Func<TSource, bool>> isExpanded) =>
        new Icon(Icons.ChevronRight, 14) { Foreground = Palette.TextSecondary }
            .Bind(Visual.RenderTransformProperty, source, isExpanded, expanded => expanded ? Expanded : Collapsed);
}
