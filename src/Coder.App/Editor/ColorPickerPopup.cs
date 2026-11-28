using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Theme;
using Coder.Core.Colors;
using HsvColor = Coder.Core.Colors.HsvColor;

namespace Coder.App.Editor;

/// <summary>
/// The color picker card, like VS Code's: a header with the new color (click to cycle hex → rgb → hsl) and
/// the original (click to go back to it), a saturation/brightness square, hue and opacity sliders and a text
/// field. It only reports changes; the editor writes them into the document. Keyboard: Tab moves between the
/// parts, arrows (Shift for bigger steps) move the square's and sliders' thumbs, Enter keeps the color, Esc
/// reverts it.
/// </summary>
public sealed class ColorPickerPopup : Border
{
    private const double CardWidth = 260;

    private readonly ColorSwatchButton _current = new() { Height = 34 };
    private readonly ColorSwatchButton _original = new() { Width = 64, Height = 34 };
    private readonly SaturationValueArea _area = new() { Height = 150 };
    private readonly ColorSlider _hue = new(ColorSliderKind.Hue) { Height = 14 };
    private readonly ColorSlider _alpha = new(ColorSliderKind.Alpha) { Height = 14 };
    private readonly TextBox _text = new() { FontFamily = Typography.Code, FontSize = Typography.SmallSize };
    private HsvColor _hsv;
    private RgbaColor _originalColor;
    private ColorStyle _style = new();
    private ColorSyntax _syntax;

    public ColorPickerPopup()
    {
        Width = CardWidth;
        Background = Palette.Elevated;
        BorderBrush = Palette.BorderStrong;
        BorderThickness = new Thickness(1);
        CornerRadius = Spacing.RadiusLarge;
        Padding = new Thickness(Spacing.S);
        ThemeManager.BindShadow(this, "0 8 24 0");
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(this, "Color Picker");

        AutomationProperties.SetName(_current, "Color format");
        ToolTip.SetTip(_current, "Click to switch between hex, rgb and hsl");
        AutomationProperties.SetName(_original, "Original color");
        ToolTip.SetTip(_original, "Click to go back to the original color");
        AutomationProperties.SetName(_area, "Saturation and brightness");
        AutomationProperties.SetName(_hue, "Hue");
        AutomationProperties.SetName(_alpha, "Opacity");
        AutomationProperties.SetName(_text, "Color value");

        _current.Click += () =>
        {
            _style = ColorLiterals.Switch(_style, ColorLiterals.Next(_style.Format));
            Changed();
        };
        _original.Click += () =>
        {
            _hsv = _originalColor.ToHsv();
            Changed();
        };
        _area.Changed += () =>
        {
            _hsv = _hsv with { S = _area.Saturation, V = _area.Value };
            Changed();
        };
        _hue.Changed += () =>
        {
            _hsv = _hsv with { H = _hue.Value * 360 };
            Changed();
        };
        _alpha.Changed += () =>
        {
            _hsv = _hsv with { A = _alpha.Value };
            Changed();
        };
        _text.LostFocus += (_, _) => ApplyText();
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = Spacing.XS };
        header.Children.Add(_current);
        Grid.SetColumn(_original, 1);
        header.Children.Add(_original);
        Child = new StackPanel
        {
            Spacing = Spacing.S,
            Children = { header, _area, _hue, _alpha, _text },
        };
    }

    /// <summary>The color changed (any part); the editor writes it in this style.</summary>
    public event Action<RgbaColor, ColorStyle>? ColorChanged;

    /// <summary>Enter: keep the color and close.</summary>
    public event Action? Committed;

    /// <summary>Esc: put the original text back and close.</summary>
    public event Action? Reverted;

    /// <summary>The color shown now.</summary>
    public RgbaColor Color => _hsv.ToRgba();

    /// <summary>The text the picked color is written as.</summary>
    public string Text => ColorLiterals.Format(Color, _style);

    /// <summary>Starts a session on a literal: its color is both the original and the current one.</summary>
    public void Show(RgbaColor color, ColorStyle style, ColorSyntax syntax)
    {
        _originalColor = color;
        _style = style;
        _syntax = syntax;
        _hsv = color.ToHsv();
        _original.Color = ToAvalonia(color);
        _original.Label = null;
        Refresh();
    }

    /// <summary>Focuses the saturation/brightness square (the picker's main part).</summary>
    public void FocusFirst() => _area.Focus(NavigationMethod.Tab);

    /// <summary>Sets the color as the user would (tests, and the text field).</summary>
    public void SetColor(RgbaColor color)
    {
        _hsv = color.ToHsv();
        Changed();
    }

    /// <summary>Cycles hex → rgb → hsl (tests, and the header).</summary>
    public void CycleFormat()
    {
        _style = ColorLiterals.Switch(_style, ColorLiterals.Next(_style.Format));
        Changed();
    }

    private void Changed()
    {
        Refresh();
        ColorChanged?.Invoke(Color, _style);
    }

    private void Refresh()
    {
        var color = Color;
        _current.Color = ToAvalonia(color);
        _current.Label = Text;
        AutomationProperties.SetHelpText(_current, $"{Text}. Click to switch to {FormatName(ColorLiterals.Next(_style.Format))}");
        _area.Set(_hsv.H, _hsv.S, _hsv.V);
        _hue.Set(_hsv.H / 360, HueColor(_hsv.H));
        _alpha.Set(_hsv.A, ToAvalonia(color with { A = 255 }));
        if (!_text.IsKeyboardFocusWithin)
        {
            _text.Text = Text;
        }
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Reverted?.Invoke();
        }
        else if (e.Key == Key.Enter && e.Source is not ColorSwatchButton)
        {
            e.Handled = true;
            if (_text.IsKeyboardFocusWithin)
            {
                ApplyText();
            }

            Committed?.Invoke();
        }
    }

    /// <summary>Takes what was typed in the field when it is a color; its notation becomes the format.</summary>
    private void ApplyText()
    {
        if (_text.Text is not { } text || text.Trim() == Text)
        {
            return;
        }

        if (ColorLiterals.TryParse(text, _syntax, out var literal))
        {
            _hsv = literal.Color.ToHsv();
            _style = literal.Style;
            Changed();
        }
        else
        {
            _text.Text = Text; // not a color: show the current one again
        }
    }

    internal static Color ToAvalonia(RgbaColor color) => Avalonia.Media.Color.FromArgb(color.A, color.R, color.G, color.B);

    private static string FormatName(ColorFormat format) => format switch
    {
        ColorFormat.Rgb => "rgb",
        ColorFormat.Hsl => "hsl",
        _ => "hex",
    };

    private static Color HueColor(double hue) => ToAvalonia(new HsvColor(hue, 1, 1).ToRgba());
}

/// <summary>Cursors shared by the picker parts.</summary>
internal static class PickerCursors
{
    public static readonly Cursor Hand = new(StandardCursorType.Hand);
    public static readonly Cursor Cross = new(StandardCursorType.Cross);
}

/// <summary>The gray squares drawn behind translucent colors.</summary>
internal static class Checkerboard
{
    public static void Draw(DrawingContext context, Rect rect, double cell)
    {
        context.FillRectangle(Palette.Elevated, rect);
        var row = 0;
        for (var y = rect.Top; y < rect.Bottom; y += cell, row++)
        {
            for (var x = rect.Left + (row % 2 == 0 ? 0 : cell); x < rect.Right; x += cell * 2)
            {
                context.FillRectangle(Palette.TextMuted, new Rect(x, y, Math.Min(cell, rect.Right - x), Math.Min(cell, rect.Bottom - y)));
            }
        }
    }
}

/// <summary>A focusable color box with an optional label: the picker header's two halves.</summary>
internal sealed class ColorSwatchButton : Control
{
    private static readonly Pen FocusPen = new(Palette.Accent, 2);
    private static readonly Pen Outline = new(Palette.BorderStrong, 1);
    private Color _color;
    private string? _label;

    public ColorSwatchButton()
    {
        Focusable = true;
        Cursor = PickerCursors.Hand;
    }

    public event Action? Click;

    public Color Color
    {
        get => _color;
        set
        {
            _color = value;
            InvalidateVisual();
        }
    }

    public string? Label
    {
        get => _label;
        set
        {
            _label = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        var radius = Spacing.RadiusMedium.TopLeft;
        using (context.PushClip(new RoundedRect(rect, radius)))
        {
            if (_color.A < 255)
            {
                Checkerboard.Draw(context, rect, 6);
            }

            context.FillRectangle(new SolidColorBrush(_color), rect);
        }

        context.DrawRectangle(null, IsFocused ? FocusPen : Outline, rect.Deflate(IsFocused ? 1 : 0.5), radius, radius);
        if (_label is { Length: > 0 } label)
        {
            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Typography.Code), Typography.SmallSize, ContrastingText(_color));
            context.DrawText(text, new Point(Math.Max(Spacing.S, (rect.Width - text.Width) / 2), (rect.Height - text.Height) / 2));
        }
    }

    /// <summary>Black or white text, whichever reads better on the color (theme text when mostly see-through).</summary>
    private static IBrush ContrastingText(Color color)
    {
        if (color.A < 128)
        {
            return Palette.TextPrimary;
        }

        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
        return luminance > 0.6 ? Brushes.Black : Brushes.White;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            Click?.Invoke();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            Click?.Invoke();
            e.Handled = true;
        }
    }
}

/// <summary>The square: saturation left to right, brightness bottom to top, for the current hue.</summary>
internal sealed class SaturationValueArea : Control
{
    private static readonly Pen ThumbInner = new(Palette.TextOnAccent, 2);
    private static readonly Pen ThumbOuter = new(Palette.Shadow, 1);
    private static readonly Pen FocusPen = new(Palette.Accent, 2);
    private double _hue;
    private bool _dragging;

    public SaturationValueArea()
    {
        Focusable = true;
        Cursor = PickerCursors.Cross;
    }

    public event Action? Changed;

    public double Saturation { get; private set; }

    public double Value { get; private set; }

    public void Set(double hue, double saturation, double value)
    {
        (_hue, Saturation, Value) = (hue, saturation, value);
        AutomationProperties.SetHelpText(this, $"Saturation {Math.Round(saturation * 100)}%, brightness {Math.Round(value * 100)}%");
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        var radius = Spacing.RadiusMedium.TopLeft;
        using (context.PushClip(new RoundedRect(rect, radius)))
        {
            context.FillRectangle(new SolidColorBrush(ColorPickerPopup.ToAvalonia(new HsvColor(_hue, 1, 1).ToRgba())), rect);
            context.FillRectangle(Gradient(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 0, RelativeUnit.Relative), Colors.White), rect);
            context.FillRectangle(Gradient(new RelativePoint(0, 1, RelativeUnit.Relative), new RelativePoint(0, 0, RelativeUnit.Relative), Colors.Black), rect);
        }

        if (IsFocused)
        {
            context.DrawRectangle(null, FocusPen, rect.Deflate(1), radius, radius);
        }

        var center = new Point(Saturation * rect.Width, (1 - Value) * rect.Height);
        context.DrawEllipse(null, ThumbOuter, center, 7, 7);
        context.DrawEllipse(null, ThumbInner, center, 5.5, 5.5);
    }

    /// <summary>Opaque <paramref name="color"/> fading to transparent.</summary>
    private static LinearGradientBrush Gradient(RelativePoint from, RelativePoint to, Color color) => new()
    {
        StartPoint = from,
        EndPoint = to,
        GradientStops = { new GradientStop(color, 0), new GradientStop(Avalonia.Media.Color.FromArgb(0, color.R, color.G, color.B), 1) },
    };

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus(NavigationMethod.Pointer);
        _dragging = true;
        e.Pointer.Capture(this);
        PickAt(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            PickAt(e.GetPosition(this));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 0.01;
        var (s, v) = e.Key switch
        {
            Key.Left => (Saturation - step, Value),
            Key.Right => (Saturation + step, Value),
            Key.Up => (Saturation, Value + step),
            Key.Down => (Saturation, Value - step),
            _ => (double.NaN, double.NaN),
        };
        if (double.IsNaN(s))
        {
            return;
        }

        Update(s, v);
        e.Handled = true;
    }

    private void PickAt(Point point) =>
        Update(point.X / Math.Max(1, Bounds.Width), 1 - point.Y / Math.Max(1, Bounds.Height));

    private void Update(double saturation, double value)
    {
        Set(_hue, Math.Clamp(saturation, 0, 1), Math.Clamp(value, 0, 1));
        Changed?.Invoke();
    }
}

internal enum ColorSliderKind
{
    Hue,
    Alpha,
}

/// <summary>A horizontal strip with a thumb: the hue spectrum, or the color fading to transparent over a checkerboard.</summary>
internal sealed class ColorSlider : Control
{
    private static readonly Pen ThumbInner = new(Palette.TextOnAccent, 2);
    private static readonly Pen ThumbOuter = new(Palette.Shadow, 1);
    private static readonly Pen FocusPen = new(Palette.Accent, 2);
    private static readonly Pen Outline = new(Palette.BorderStrong, 1);
    private readonly ColorSliderKind _kind;
    private Color _color;
    private bool _dragging;

    public ColorSlider(ColorSliderKind kind)
    {
        _kind = kind;
        Focusable = true;
        Cursor = PickerCursors.Hand;
    }

    public event Action? Changed;

    /// <summary>0..1: the hue as a fraction of the circle, or the opacity.</summary>
    public double Value { get; private set; }

    public void Set(double value, Color color)
    {
        Value = value;
        _color = color;
        AutomationProperties.SetHelpText(this, _kind == ColorSliderKind.Hue ? $"Hue {Math.Round(value * 360)} degrees" : $"Opacity {Math.Round(value * 100)}%");
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        var radius = rect.Height / 2;
        using (context.PushClip(new RoundedRect(rect, radius)))
        {
            if (_kind == ColorSliderKind.Alpha)
            {
                Checkerboard.Draw(context, rect, rect.Height / 2);
            }

            context.FillRectangle(Fill(), rect);
        }

        context.DrawRectangle(null, IsFocused ? FocusPen : Outline, rect.Deflate(IsFocused ? 1 : 0.5), radius, radius);
        var x = Math.Clamp(Value * rect.Width, radius, rect.Width - radius);
        var center = new Point(x, rect.Height / 2);
        context.DrawEllipse(null, ThumbOuter, center, radius + 1, radius + 1);
        context.DrawEllipse(null, ThumbInner, center, radius - 0.5, radius - 0.5);
    }

    private LinearGradientBrush Fill()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        };
        if (_kind == ColorSliderKind.Hue)
        {
            for (var i = 0; i <= 6; i++)
            {
                brush.GradientStops.Add(new GradientStop(ColorPickerPopup.ToAvalonia(new HsvColor(i * 60, 1, 1).ToRgba()), i / 6.0));
            }
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(Avalonia.Media.Color.FromArgb(0, _color.R, _color.G, _color.B), 0));
            brush.GradientStops.Add(new GradientStop(_color, 1));
        }

        return brush;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus(NavigationMethod.Pointer);
        _dragging = true;
        e.Pointer.Capture(this);
        Update(e.GetPosition(this).X / Math.Max(1, Bounds.Width));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            Update(e.GetPosition(this).X / Math.Max(1, Bounds.Width));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : _kind == ColorSliderKind.Hue ? 1 / 360.0 : 0.01;
        double? value = e.Key switch
        {
            Key.Left or Key.Down => Value - step,
            Key.Right or Key.Up => Value + step,
            Key.Home => 0,
            Key.End => 1,
            _ => null,
        };
        if (value is { } v)
        {
            Update(v);
            e.Handled = true;
        }
    }

    private void Update(double value)
    {
        Set(Math.Clamp(value, 0, 1), _color);
        Changed?.Invoke();
    }
}
