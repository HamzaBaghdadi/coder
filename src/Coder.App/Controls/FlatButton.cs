using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Coder.App.Theme;

namespace Coder.App.Controls;

/// <summary>
/// The app's borderless button, used for icon buttons (toolbars, activity bar, tab close) and,
/// with the <c>primary</c> class, for filled call-to-action buttons. Its background has the brand's
/// cut corners (<see cref="Chamfer"/>, <see cref="ChamferCorners"/>), set per kind of button in
/// <see cref="AppStyles"/> along with every state.
/// Icon-only instances must get an accessible name (the icon constructor requires one).
/// </summary>
public class FlatButton : Button
{
    public static readonly StyledProperty<double> ChamferProperty =
        ChamferBorder.ChamferProperty.AddOwner<FlatButton>();

    public static readonly StyledProperty<ChamferCorners> ChamferCornersProperty =
        ChamferBorder.CornersProperty.AddOwner<FlatButton>();

    /// <summary>Space around the icon in an icon button (both sides together).</summary>
    private const double IconButtonPadding = 10;

    private static readonly FuncControlTemplate<FlatButton> DefaultTemplate = new((button, _) => new ChamferBorder
    {
        Name = "PART_Root",
        BorderThickness = 1,
        [!ChamferBorder.BackgroundProperty] = button[!BackgroundProperty],
        [!ChamferBorder.BorderBrushProperty] = button[!BorderBrushProperty],
        [!ChamferBorder.ChamferProperty] = button[!ChamferProperty],
        [!ChamferBorder.CornersProperty] = button[!ChamferCornersProperty],
        [!Decorator.PaddingProperty] = button[!PaddingProperty],
        Child = new ContentPresenter
        {
            Name = "PART_ContentPresenter",
            [!ContentPresenter.ContentProperty] = button[!ContentProperty],
            [!HorizontalAlignmentProperty] = button[!HorizontalContentAlignmentProperty],
            [!VerticalAlignmentProperty] = button[!VerticalContentAlignmentProperty],
        },
    });

    public FlatButton()
    {
        // Colors, padding, corners and states come from styles (AppStyles), so they are not set locally here.
        Template = DefaultTemplate;
        Transitions =
        [
            new BrushTransition { Property = BackgroundProperty, Duration = TimeSpan.FromMilliseconds(120) },
        ];
    }

    /// <summary>An icon-only button. It is square by default (icon plus padding), so it never stretches
    /// to its container's height; set Width/Height to size it differently.</summary>
    public FlatButton(Geometry icon, string accessibleName, double iconSize = 16)
        : this()
    {
        Width = Height = iconSize + IconButtonPadding;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Content = new Icon(icon, iconSize);
        AutomationProperties.SetName(this, accessibleName);
        ToolTip.SetTip(this, accessibleName);
    }

    /// <summary>Length of the cut corners; 0 gives a plain rectangle.</summary>
    public double Chamfer
    {
        get => GetValue(ChamferProperty);
        set => SetValue(ChamferProperty, value);
    }

    public ChamferCorners ChamferCorners
    {
        get => GetValue(ChamferCornersProperty);
        set => SetValue(ChamferCornersProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(FlatButton);
}
