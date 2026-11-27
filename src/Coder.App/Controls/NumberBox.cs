using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Coder.App.Mvvm;
using Coder.App.Theme;

namespace Coder.App.Controls;

/// <summary>
/// A whole-number field in the app's design (replacing Fluent's NumericUpDown): the number on the left of a
/// rounded field, two compact chevrons stacked on the right. Up/Down arrows and the mouse wheel step it, Enter
/// or leaving the field commits typed text, and the value always stays between <see cref="Minimum"/> and
/// <see cref="Maximum"/>. Styled by the <c>number-box</c> class in <see cref="AppStyles"/>.
/// </summary>
public sealed class NumberBox : Border
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<NumberBox, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<NumberBox, double>(nameof(Minimum), double.MinValue);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<NumberBox, double>(nameof(Maximum), double.MaxValue);

    private readonly TextBox _text;

    public NumberBox()
    {
        Classes.Add("number-box");
        _text = new TextBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 0,
            Padding = new Thickness(Spacing.S + Spacing.XXS, 0, 0, 0),
        };

        var up = Stepper(Icons.ChevronUp, "Increase", +1);
        var down = Stepper(Icons.ChevronDown, "Decrease", -1);
        Child = new DockPanel
        {
            Children =
            {
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, Spacing.XXS, 0),
                    Children = { up, down },
                }.Dock(Dock.Right),
                _text,
            },
        };

        _text.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Up:
                    Step(+1);
                    e.Handled = true;
                    break;
                case Key.Down:
                    Step(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    Commit();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    ShowValue(); // undo the typing
                    break;
            }
        };
        _text.LostFocus += (_, _) => Commit();
        PointerWheelChanged += (_, e) =>
        {
            if (_text.IsFocused)
            {
                Step(e.Delta.Y > 0 ? +1 : -1);
                e.Handled = true;
            }
        };
        ShowValue();
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>The text field inside (tests type into it).</summary>
    internal TextBox Text => _text;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
        {
            ShowValue();
        }
        else if (change.Property == AutomationProperties.NameProperty)
        {
            AutomationProperties.SetName(_text, AutomationProperties.GetName(this)); // screen readers land in the field
        }
    }

    private FlatButton Stepper(Avalonia.Media.Geometry icon, string name, int direction)
    {
        var button = new FlatButton(icon, name, iconSize: 11)
        {
            Width = 20,
            Height = 13,
            Padding = new Thickness(0),
            Radius = Spacing.RadiusSmall.TopLeft,
            Focusable = false, // the keyboard steps with Up/Down in the field
            Command = new Presentation.Mvvm.RelayCommand(() => Step(direction)),
        };
        return button;
    }

    private void Step(int direction)
    {
        Commit();
        Value = Clamp(Math.Round(Value) + direction);
    }

    private void Commit()
    {
        if (double.TryParse(_text.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var typed)
            || double.TryParse(_text.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out typed))
        {
            Value = Clamp(Math.Round(typed));
        }

        ShowValue(); // also restores the number when the text wasn't one
    }

    private double Clamp(double value) => Math.Clamp(value, Minimum, Math.Max(Minimum, Maximum));

    private void ShowValue() => _text.Text = Value.ToString("0", CultureInfo.CurrentCulture);
}
