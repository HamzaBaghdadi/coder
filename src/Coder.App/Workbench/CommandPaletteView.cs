using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
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
            Height = 32,
            Margin = new Thickness(Spacing.S),
            Padding = new Thickness(Spacing.S, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
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
            Margin = new Thickness(Spacing.XS, 0, Spacing.XS, Spacing.XS),
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
            Child = new StackPanel { Children = { _input, _results } },
        };
        ThemeManager.BindShadow(card, "0 12 32 0");

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
            Height = 28,
            Margin = new Thickness(Spacing.S, 0),
            Children =
            {
                WelcomeView.KeyHint(item.Hint).Dock(Dock.Right),
                text,
            },
        };
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
