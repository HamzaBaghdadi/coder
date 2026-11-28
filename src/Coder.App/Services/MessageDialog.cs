using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Theme;

namespace Coder.App.Services;

/// <summary>A button in a <see cref="MessageDialog{T}"/>: its label and the result it returns.</summary>
internal sealed record DialogChoice<T>(string Label, T Result, bool IsPrimary = false);

/// <summary>Whether an in-app modal is open, so the window's global shortcuts stay out of its way.</summary>
internal static class Modal
{
    public static int OpenCount { get; set; }

    public static bool IsOpen => OpenCount > 0;
}

/// <summary>
/// A small modal question drawn inside the window, in the app's own design: a scrim dims the window and a
/// rounded card shows a message, an explanation and a row of choices. The primary choice is the default
/// button and has focus; Tab stays inside the card; Escape returns <c>cancelResult</c>.
/// </summary>
internal sealed class MessageDialog<T> : Border
{
    private readonly TaskCompletionSource<T> _result = new();
    private readonly T _cancelResult;
    private readonly FlatButton? _primary;
    private IInputElement? _focusBefore;
    private OverlayLayer? _layer;

    public MessageDialog(string message, string detail, IReadOnlyList<DialogChoice<T>> choices, T cancelResult)
    {
        _cancelResult = cancelResult;
        Background = Palette.Shadow; // the scrim: deep on dark themes, soft on light ones
        Opacity = 0;
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(120) }];
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, Spacing.S, 0, 0),
            Spacing = Spacing.S,
        };
        foreach (var choice in choices)
        {
            var button = CreateButton(choice);
            _primary = choice.IsPrimary ? button : _primary;
            buttons.Children.Add(button);
        }

        var card = new Border
        {
            Classes = { "card" },
            Width = 460,
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Spacing.L),
            Padding = new Thickness(Spacing.XL),
            Child = new StackPanel
            {
                Spacing = Spacing.M,
                Children =
                {
                    new TextBlock { Text = message, Classes = { "title" }, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = detail, Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, LineHeight = 20 },
                    buttons,
                },
            },
        };
        ThemeManager.BindShadow(card, "0 16 48 0");
        AutomationProperties.SetName(card, message);
        Child = card;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(_cancelResult);
                e.Handled = true;
            }
        };

        // Clicks on the scrim do nothing (like VS Code): the question must be answered.
        PointerPressed += (_, e) => e.Handled = true;
    }

    /// <summary>The card's buttons, in order (for tests).</summary>
    internal IEnumerable<FlatButton> Buttons => ((StackPanel)((StackPanel)((Border)Child!).Child!).Children[2]).Children.OfType<FlatButton>();

    /// <summary>Shows the dialog over <paramref name="owner"/> and returns the chosen result.</summary>
    public Task<T> ShowAsync(Visual owner)
    {
        _layer = OverlayLayer.GetOverlayLayer(owner) ?? throw new InvalidOperationException("The window has no overlay layer.");
        _focusBefore = TopLevel.GetTopLevel(owner)?.FocusManager?.GetFocusedElement();

        // The overlay layer is a canvas: stretch the scrim over all of it, and keep it that size.
        Width = _layer.Bounds.Width;
        Height = _layer.Bounds.Height;
        _layer.PropertyChanged += OnLayerResized;
        _layer.Children.Add(this);
        Modal.OpenCount++;

        Opacity = 1;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => (_primary ?? Buttons.FirstOrDefault())?.Focus(NavigationMethod.Tab),
            Avalonia.Threading.DispatcherPriority.Loaded);
        return _result.Task;
    }

    private void OnLayerResized(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty && _layer is not null)
        {
            Width = _layer.Bounds.Width;
            Height = _layer.Bounds.Height;
        }
    }

    /// <summary>A dialog that leaves the window unanswered (the window closed) counts as cancelled.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Close(_cancelResult);
    }

    /// <summary>Closes the dialog as if dismissed (the question was withdrawn), returning <c>cancelResult</c>.</summary>
    public void Dismiss() => Close(_cancelResult);

    private void Close(T result)
    {
        if (_layer is not { } layer)
        {
            return;
        }

        _layer = null; // first, so the removal below (which detaches this) doesn't close twice
        layer.PropertyChanged -= OnLayerResized;
        layer.Children.Remove(this);
        Modal.OpenCount--;
        _focusBefore?.Focus();
        _result.TrySetResult(result);
    }

    private FlatButton CreateButton(DialogChoice<T> choice)
    {
        var button = new FlatButton
        {
            Content = choice.Label,
            IsDefault = choice.IsPrimary,
            Padding = new Thickness(Spacing.L, 6),
        };

        // The primary button takes its colors from the "primary" style. Setting its Foreground here, even to
        // null, would be a local value that wins over the style and leaves its label invisible.
        button.Classes.Add(choice.IsPrimary ? "primary" : "secondary");
        button.Click += (_, _) => Close(choice.Result);
        return button;
    }
}
