using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Coder.App.Theme;

/// <summary>
/// The app's scroll bar, replacing Fluent's arrowed one: a thin transparent track with a rounded
/// thumb that brightens on hover and while dragged, like VS Code and Zed. Clicking the track pages.
/// Colors come from the theme tokens.
/// </summary>
public static class ScrollBarTheme
{
    /// <summary>The standard scroll bar: 12px with the thumb inset 3px from the track's edges.</summary>
    public static ControlTheme Create() => Create(thickness: 12, thumbInset: 3);

    /// <summary>
    /// A slim scroll bar for strips that scroll sideways (editor tabs), laid over their bottom edge like
    /// VS Code's: 5px, with the thumb inset 1px.
    /// </summary>
    public static readonly ControlTheme Slim = Create(thickness: 5, thumbInset: 1);

    private static ControlTheme Create(double thickness, double thumbInset) => new(typeof(ScrollBar))
    {
        Setters =
        {
            new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ScrollBar>((bar, scope) => Template(bar, scope, thumbInset))),
            new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
        },
        Children =
        {
            new Style(x => x.Nesting().Class(":vertical")) { Setters = { new Setter(Layoutable.WidthProperty, thickness) } },
            new Style(x => x.Nesting().Class(":horizontal")) { Setters = { new Setter(Layoutable.HeightProperty, thickness) } },
        },
    };

    private static Control Template(ScrollBar bar, INameScope scope, double thumbInset)
    {
        var track = new Track
        {
            Name = "PART_Track",
            [!Track.MinimumProperty] = bar[!RangeBase.MinimumProperty],
            [!Track.MaximumProperty] = bar[!RangeBase.MaximumProperty],
            // The track only shows the value; drags are handled below. When the track wrote dragged values back
            // through a two-way binding, that set a local value on the scroll bar which cut it off from its
            // ScrollViewer, so after a drag the thumb stopped following wheel scrolling.
            [!Track.ValueProperty] = bar[!RangeBase.ValueProperty],
            IgnoreThumbDrag = true,
            [!Track.ViewportSizeProperty] = bar[!ScrollBar.ViewportSizeProperty],
            [!Track.OrientationProperty] = bar[!ScrollBar.OrientationProperty],
            Thumb = new Thumb { Theme = ThumbTheme, Padding = new Avalonia.Thickness(thumbInset) },

            // The track itself: clicking above or below the thumb pages, like any scroll bar.
            DecreaseButton = PageButton("PART_PageUpButton", scope),
            IncreaseButton = PageButton("PART_PageDownButton", scope),
        };

        FollowPointer(bar, track, track.Thumb);

        // Vertical tracks grow downwards (value 0 at the top).
        void FollowOrientation() => track.IsDirectionReversed = bar.Orientation == Orientation.Vertical;
        FollowOrientation();
        bar.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollBar.OrientationProperty)
            {
                FollowOrientation();
            }
        };
        scope.Register("PART_Track", track);
        return new Border { [!Border.BackgroundProperty] = bar[!TemplatedControl.BackgroundProperty], Child = track };
    }

    /// <summary>
    /// Dragging keeps the grabbed point of the thumb under the pointer. The value is set from where the
    /// drag started plus the total distance moved, measured on the track (which doesn't move), never by
    /// adding up steps: steps measured on the moving thumb add up wrong when moves outrun layout.
    /// The scroll bar's current value is set, so it tells its ScrollViewer and its binding to the
    /// ScrollViewer stays in place (wheel scrolling keeps moving the thumb afterwards).
    /// </summary>
    private static void FollowPointer(ScrollBar bar, Track track, Thumb thumb)
    {
        Point? start = null;
        var startValue = 0.0;
        thumb.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(thumb).Properties.IsLeftButtonPressed)
            {
                start = e.GetPosition(track);
                startValue = bar.Value;
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        thumb.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (start is { } from)
            {
                var at = e.GetPosition(track);
                var value = startValue + track.ValueFromDistance(at.X - from.X, at.Y - from.Y);
                bar.SetCurrentValue(RangeBase.ValueProperty, Math.Clamp(value, bar.Minimum, bar.Maximum));
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        thumb.AddHandler(InputElement.PointerReleasedEvent, (_, _) => start = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        thumb.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => start = null, handledEventsToo: true);
    }

    private static RepeatButton PageButton(string name, INameScope scope)
    {
        var button = new RepeatButton { Name = name, Focusable = false, Theme = PageButtonTheme };
        scope.Register(name, button);
        return button;
    }

    private static readonly ControlTheme PageButtonTheme = new(typeof(RepeatButton))
    {
        Setters =
        {
            new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<RepeatButton>((_, _) => new Border { Background = Brushes.Transparent })),
        },
    };

    /// <summary>A pill inset from the track's edges; brighter on hover, brighter still while dragged.</summary>
    private static readonly ControlTheme ThumbTheme = new(typeof(Thumb))
    {
        Setters =
        {
            new Setter(TemplatedControl.BackgroundProperty, Palette.ScrollbarThumb),
            new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<Thumb>((thumb, _) => new Border
            {
                [!Border.MarginProperty] = thumb[!TemplatedControl.PaddingProperty],
                CornerRadius = new CornerRadius(3),
                [!Border.BackgroundProperty] = thumb[!TemplatedControl.BackgroundProperty],
            })),
        },
        Children =
        {
            new Style(x => x.Nesting().Class(":pointerover")) { Setters = { new Setter(TemplatedControl.BackgroundProperty, Palette.ScrollbarThumbHover) } },
            new Style(x => x.Nesting().Class(":pressed")) { Setters = { new Setter(TemplatedControl.BackgroundProperty, Palette.ScrollbarThumbActive) } },
        },
    };
}
