using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Theme;

namespace Coder.App.Services;

/// <summary>A button in a <see cref="MessageDialog{T}"/>: its label and the result it returns.</summary>
internal sealed record DialogChoice<T>(string Label, T Result, bool IsPrimary = false);

/// <summary>
/// A small modal question: a message, an explanation and a row of choices. The primary choice is the
/// default button and has focus; Escape returns <c>cancelResult</c>.
/// </summary>
internal sealed class MessageDialog<T> : Window
{
    public MessageDialog(string message, string detail, IReadOnlyList<DialogChoice<T>> choices, T cancelResult)
    {
        Title = "Coder";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Elevated;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = Spacing.S,
        };
        FlatButton? primary = null;
        foreach (var choice in choices)
        {
            var button = CreateButton(choice);
            primary = choice.IsPrimary ? button : primary;
            buttons.Children.Add(button);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(Spacing.XL),
            Spacing = Spacing.L,
            Children =
            {
                new TextBlock { Text = message, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = detail, Foreground = Palette.TextSecondary, TextWrapping = TextWrapping.Wrap },
                buttons,
            },
        };

        Opened += (_, _) => primary?.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(cancelResult);
            }
        };
    }

    private FlatButton CreateButton(DialogChoice<T> choice)
    {
        var button = new FlatButton
        {
            Content = choice.Label,
            IsDefault = choice.IsPrimary,
            Padding = new Thickness(Spacing.L, Spacing.S),
        };

        // The primary button takes its colors from the "primary" style. Setting its Foreground here, even to
        // null, would be a local value that wins over the style and leaves its label invisible.
        if (choice.IsPrimary)
        {
            button.Classes.Add("primary");
        }
        else
        {
            button.Foreground = Palette.TextPrimary;
        }

        button.Click += (_, _) => Close(choice.Result);
        return button;
    }
}
