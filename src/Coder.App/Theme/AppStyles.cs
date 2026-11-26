using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Styling;
using Coder.App.Controls;

namespace Coder.App.Theme;

/// <summary>
/// Application-wide styles, written in C#. Fluent provides the base controls; these styles apply the
/// design tokens and define every interactive state (hover, pressed, selected, focused) explicitly.
/// </summary>
public static class AppStyles
{
    /// <summary>Container theme for list rows (explorer, palette): flat, full-width, token colors.</summary>
    public static readonly ControlTheme ListRowTheme = CreateListRowTheme();

    public static void Apply(Application app)
    {
        app.Resources[typeof(ScrollBar)] = ScrollBarTheme.Create(); // every scroll bar, including Fluent's ScrollViewer

        app.Styles.AddRange(
        [
            // Strips that scroll sideways (editor tabs) get the slim scroll bar over their bottom edge.
            Rule(x => x.OfType<ScrollViewer>().Class("slim").Template().OfType<ScrollBar>(),
                (StyledElement.ThemeProperty, ScrollBarTheme.Slim)),

            Rule<Window>(
                (TemplatedControl.BackgroundProperty, Palette.EditorBackground),
                (TemplatedControl.FontFamilyProperty, Typography.Ui),
                (TemplatedControl.FontSizeProperty, Typography.UiSize),
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary)),

            // Flat buttons.
            // Clickable things show the hand (pointer) cursor.
            Rule<FlatButton>((InputElement.CursorProperty, new Cursor(StandardCursorType.Hand))),
            Rule<FlatButton>(
                (TemplatedControl.BackgroundProperty, Palette.HoverClear),
                (TemplatedControl.BorderBrushProperty, Brushes.Transparent),
                (TemplatedControl.ForegroundProperty, Palette.TextSecondary),
                (FlatButton.ChamferProperty, Spacing.ChamferMedium),
                (TemplatedControl.PaddingProperty, new Thickness(4)),
                (ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                (ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center)),
            Rule(x => x.OfType<FlatButton>().Class(":pointerover"),
                (TemplatedControl.BackgroundProperty, Palette.Hover),
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary)),
            Rule(x => x.OfType<FlatButton>().Class(":pressed"),
                (TemplatedControl.BackgroundProperty, Palette.Pressed)),
            Rule(x => x.OfType<FlatButton>().Class(":disabled"),
                (Visual.OpacityProperty, 0.4)),
            Rule(x => x.OfType<FlatButton>().Class("active"),
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary)),
            Rule(x => x.OfType<FlatButton>().Class(":focus-visible"),
                (TemplatedControl.BorderBrushProperty, Palette.Accent)),

            // Primary call-to-action button: the logo's blue sweep, darkened for readable white text.
            Rule(x => x.OfType<FlatButton>().Class("primary"),
                (TemplatedControl.BackgroundProperty, Brand.ButtonGradient),
                (TemplatedControl.ForegroundProperty, Palette.TextOnAccent),
                (TemplatedControl.FontWeightProperty, FontWeight.SemiBold),
                (TemplatedControl.PaddingProperty, new Thickness(16, 8)),
                (FlatButton.ChamferProperty, Spacing.ChamferLarge)),
            Rule(x => x.OfType<FlatButton>().Class("primary").Class(":pointerover"),
                (TemplatedControl.BackgroundProperty, Brand.ButtonGradientHover),
                (TemplatedControl.ForegroundProperty, Palette.TextOnAccent)),

            // Title bar command center: a field-like button that opens quick-open.
            Rule(x => x.OfType<FlatButton>().Class("command-center"),
                (TemplatedControl.BackgroundProperty, Palette.Elevated),
                (TemplatedControl.BorderBrushProperty, Palette.BorderStrong),
                (FlatButton.ChamferProperty, Spacing.ChamferMedium)),
            Rule(x => x.OfType<FlatButton>().Class("command-center").Class(":pointerover"),
                (TemplatedControl.BackgroundProperty, Palette.Hover),
                (TemplatedControl.BorderBrushProperty, Palette.InputBorder)),

            // Window caption buttons; close turns red like the platform convention.
            Rule(x => x.OfType<FlatButton>().Class("caption"),
                (FlatButton.ChamferProperty, 0.0),
                (TemplatedControl.BorderBrushProperty, Brushes.Transparent)),
            Rule(x => x.OfType<FlatButton>().Class("caption").Class("close").Class(":pointerover"),
                (TemplatedControl.BackgroundProperty, Palette.Danger),
                (TemplatedControl.ForegroundProperty, Brushes.White)),

            // Toggle buttons (find options): small cuts, and an "on" state that does not rely on color alone.
            Rule(x => x.OfType<FlatButton>().Class("toggle"),
                (FlatButton.ChamferProperty, Spacing.ChamferSmall)),
            Rule(x => x.OfType<FlatButton>().Class("toggle").Class("checked"),
                (TemplatedControl.BackgroundProperty, Palette.Selected),
                (TemplatedControl.BorderBrushProperty, Palette.Accent),
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary)),

            // Lists.
            Rule<ListBox>(
                (TemplatedControl.BackgroundProperty, Brushes.Transparent),
                (TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                (TemplatedControl.PaddingProperty, new Thickness(0))),
            Rule(x => Selectors.Or(
                    x.OfType<ListBox>().Class(":focus-within").Descendant().OfType<ListBoxItem>().Class(":selected"),
                    x.OfType<ListBox>().Class("palette").Descendant().OfType<ListBoxItem>().Class(":selected")),
                (TemplatedControl.BackgroundProperty, Palette.Selected),
                (TemplatedControl.BorderBrushProperty, Palette.Accent)),

            // The native frame stays (Windows animates it, snaps it, and draws its border); the title bar and
            // caption buttons are Coder's own, so Avalonia draws none over them.
            Rule(x => x.OfType<Avalonia.Controls.Chrome.WindowDrawnDecorations>(), (Avalonia.Controls.Chrome.WindowDrawnDecorations.TemplateProperty, new NoDrawnDecorations())),

            // Context menus: an elevated, rounded surface in the theme's colors.
            Rule<ContextMenu>(
                (TemplatedControl.BackgroundProperty, Palette.Elevated),
                (TemplatedControl.BorderBrushProperty, Palette.BorderStrong),
                (TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                (TemplatedControl.CornerRadiusProperty, Spacing.RadiusMedium),
                (TemplatedControl.PaddingProperty, new Thickness(4))),
            Rule<MenuItem>(
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary),
                (TemplatedControl.FontSizeProperty, Typography.UiSize),
                (TemplatedControl.CornerRadiusProperty, new CornerRadius(4)),
                (TemplatedControl.PaddingProperty, new Thickness(10, 4))),
            Rule(x => Selectors.Or(x.OfType<MenuItem>().Class(":pointerover"), x.OfType<MenuItem>().Class(":selected")),
                (TemplatedControl.BackgroundProperty, Palette.Selected)),
            Rule(x => x.OfType<MenuItem>().Class("accent"), (TemplatedControl.ForegroundProperty, Palette.Accent), (TemplatedControl.FontWeightProperty, FontWeight.SemiBold)),
            Rule(x => x.OfType<Separator>(),
                (TemplatedControl.BackgroundProperty, Palette.Border),
                (Layoutable.MarginProperty, new Thickness(6, 4))),

            // Tooltips.
            Rule<ToolTip>(
                (TemplatedControl.BackgroundProperty, Palette.Elevated),
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary),
                (TemplatedControl.BorderBrushProperty, Palette.BorderStrong),
                (TemplatedControl.FontSizeProperty, Typography.SmallSize),
                (TemplatedControl.CornerRadiusProperty, Spacing.RadiusMedium),
                (TemplatedControl.PaddingProperty, new Thickness(8, 4))),

            // Text boxes: flat field that lights up with the accent when focused.
            Rule<TextBox>(
                (TemplatedControl.BackgroundProperty, Palette.EditorBackground),
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary),
                (TemplatedControl.BorderBrushProperty, Palette.InputBorder),
                (TemplatedControl.CornerRadiusProperty, Spacing.RadiusMedium),
                (TextBox.SelectionBrushProperty, Palette.TextSelection),
                (TextBox.CaretBrushProperty, Palette.Caret)),
            Rule(x => x.OfType<TextBox>().Class(":pointerover").Template().OfType<Border>().Name("PART_BorderElement"),
                (Border.BackgroundProperty, Palette.EditorBackground),
                (Border.BorderBrushProperty, Palette.BorderStrong)),
            Rule(x => x.OfType<TextBox>().Class(":focus").Template().OfType<Border>().Name("PART_BorderElement"),
                (Border.BackgroundProperty, Palette.EditorBackground),
                (Border.BorderBrushProperty, Palette.Accent),
                (Border.BorderThicknessProperty, new Thickness(1))),

            // Thin splitters that show the accent while dragging.
            Rule<GridSplitter>((TemplatedControl.BackgroundProperty, Brushes.Transparent)),
            Rule(x => x.OfType<GridSplitter>().Class(":pointerover"), (TemplatedControl.BackgroundProperty, Palette.Accent)),
        ]);
    }

    private static ControlTheme CreateListRowTheme()
    {
        var template = new FuncControlTemplate<ListBoxItem>((item, _) => new Border
        {
            Name = "PART_Root",
            [!Border.BackgroundProperty] = item[!TemplatedControl.BackgroundProperty],
            [!Border.BorderBrushProperty] = item[!TemplatedControl.BorderBrushProperty],
            BorderThickness = new Thickness(1),
            Child = new ContentPresenter
            {
                Name = "PART_ContentPresenter",
                [!ContentPresenter.ContentProperty] = item[!ContentControl.ContentProperty],
                [!ContentPresenter.ContentTemplateProperty] = item[!ContentControl.ContentTemplateProperty],
                [!ContentPresenter.PaddingProperty] = item[!TemplatedControl.PaddingProperty],
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        });

        return new ControlTheme(typeof(ListBoxItem))
        {
            Setters =
            {
                new Setter(TemplatedControl.TemplateProperty, template),
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                new Setter(TemplatedControl.BorderBrushProperty, Brushes.Transparent),
                new Setter(TemplatedControl.ForegroundProperty, Palette.TextPrimary),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                new Setter(Layoutable.MinHeightProperty, Spacing.ListRowHeight),
                new Setter(InputElement.CursorProperty, new Cursor(StandardCursorType.Hand)),
            },
            Children =
            {
                Rule(x => x.Nesting().Class(":pointerover"), (TemplatedControl.BackgroundProperty, Palette.Hover)),
                Rule(x => x.Nesting().Class(":selected"), (TemplatedControl.BackgroundProperty, Palette.SelectedInactive)),
                Rule(x => x.Nesting().Class("drop-target"),
                    (TemplatedControl.BackgroundProperty, Palette.Hover),
                    (TemplatedControl.BorderBrushProperty, Palette.Accent)),
            },
        };
    }

    private static Style Rule<T>(params (AvaloniaProperty Property, object? Value)[] setters)
        where T : Control =>
        Rule(x => x.OfType<T>(), setters);

    private static Style Rule(Func<Selector?, Selector> selector, params (AvaloniaProperty Property, object? Value)[] setters)
    {
        var style = new Style(selector);
        foreach (var (property, value) in setters)
        {
            style.Setters.Add(new Setter(property, value));
        }

        return style;
    }
}
