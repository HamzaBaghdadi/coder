using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Palette;

namespace Coder.App.Workbench;

/// <summary>
/// The quick-input overlay: a floating card near the top with a search field and ranked results.
/// Fully keyboard-driven (type, ↑/↓, Enter, Esc); clicking outside closes it.
/// </summary>
public sealed class CommandPaletteView : Panel
{
    private readonly CommandPaletteViewModel _palette;
    private readonly TextBox _input;
    private readonly ListBox _results;

    public CommandPaletteView(CommandPaletteViewModel palette)
    {
        _palette = palette;
        this.Bind(IsVisibleProperty, palette, x => x.IsOpen);

        _input = new TextBox
        {
            Height = 44,
            Padding = new Thickness(Spacing.M + 1, 0), // the query lines up with the row labels below
            FontSize = Typography.UiSize,
            VerticalContentAlignment = VerticalAlignment.Center,
            Styles = { BorderlessField() },
        }
        .Bind(TextBox.TextProperty, palette, x => x.Query, Avalonia.Data.BindingMode.TwoWay)
        .Bind(TextBox.PlaceholderTextProperty, palette, x => x.Placeholder);
        AutomationProperties.SetName(_input, "Command palette input");
        _input.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _results = new ListBox
        {
            Classes = { "palette" },
            ItemsSource = palette.Items,
            MaxHeight = 380,
            Margin = new Thickness(0, Spacing.XS),
            ItemContainerTheme = AppStyles.ListRowTheme,
            ItemTemplate = Templates.Row<PaletteItem>(CreateRow),
            Focusable = false,
        }.Bind(ListBox.SelectedItemProperty, palette, x => x.SelectedItem, Avalonia.Data.BindingMode.TwoWay);
        AutomationProperties.SetName(_results, "Results");
        _results.Tapped += async (_, e) =>
        {
            if (e.Source is Visual { DataContext: PaletteItem item })
            {
                await _palette.AcceptAsync(item);
            }
        };

        var card = new Border
        {
            Width = 600,
            Margin = new Thickness(0, Spacing.TitleBarHeight + 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Palette.Elevated,
            BorderBrush = Palette.BorderStrong,
            BorderThickness = new Thickness(1),
            CornerRadius = Spacing.RadiusLarge,
            ClipToBounds = true,
            Child = new StackPanel
            {
                Children =
                {
                    _input,
                    new Border { Height = 1, Background = Palette.BorderStrong }, // hairline between field and results
                    _results,
                },
            },
        };
        ThemeManager.BindShadow(card, "0 8 24 0");

        // A transparent backdrop catches clicks outside the card.
        var backdrop = new Border { Background = Brushes.Transparent };
        backdrop.PointerPressed += (_, _) => _palette.Close();

        Children.Add(backdrop);
        Children.Add(card);

        palette.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && palette.IsOpen)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _input.Focus();
                    _input.CaretIndex = _input.Text?.Length ?? 0;
                });
            }

            if (e.PropertyName == nameof(CommandPaletteViewModel.SelectedItem) && palette.SelectedItem is { } selected)
            {
                _results.ScrollIntoView(selected);
            }
        };
    }

    private static Control CreateRow(PaletteItem item)
    {
        var text = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Spacing.S,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                item.FilePath is { } file ? FileIcons.File(file) : new Panel { IsVisible = false },
                new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = item.Detail,
                    FontSize = Typography.SmallSize,
                    Foreground = Palette.TextSecondary,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };

        return new DockPanel
        {
            Height = 30,
            Margin = new Thickness(Spacing.S, 0),
            Children =
            {
                WelcomeView.KeyHint(item.Hint).Dock(Dock.Right),
                text,
            },
        };
    }

    /// <summary>
    /// Local styles that make the query field part of the card: no fill, no border and no focus halo in
    /// any state (the caret and the card itself show where typing goes).
    /// </summary>
    internal static Styles BorderlessField()
    {
        var border = new Style(x => x.OfType<TextBox>().Template().OfType<Border>().Name("PART_BorderElement"))
        {
            Setters =
            {
                new Setter(Border.BackgroundProperty, Brushes.Transparent),
                new Setter(Border.BorderBrushProperty, Brushes.Transparent),
                new Setter(Border.BorderThicknessProperty, new Thickness(0)),
                new Setter(Border.BoxShadowProperty, default(BoxShadows)),
            },
        };
        var states = new Style(x => Selectors.Or(
            x.OfType<TextBox>().Class(":pointerover").Template().OfType<Border>().Name("PART_BorderElement"),
            x.OfType<TextBox>().Class(":focus").Template().OfType<Border>().Name("PART_BorderElement")))
        {
            Setters =
            {
                new Setter(Border.BackgroundProperty, Brushes.Transparent),
                new Setter(Border.BorderBrushProperty, Brushes.Transparent),
                new Setter(Border.BorderThicknessProperty, new Thickness(0)),
                new Setter(Border.BoxShadowProperty, default(BoxShadows)),
            },
        };
        return [border, states];
    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _palette.Close();
                break;
            case Key.Down:
                _palette.MoveSelection(1);
                break;
            case Key.Up:
                _palette.MoveSelection(-1);
                break;
            case Key.Enter:
                e.Handled = true;
                await _palette.AcceptAsync();
                return;
            default:
                return;
        }

        e.Handled = true;
    }
}
