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
/// <para>
/// Shape language: ordinary controls are rounded (<see cref="Spacing.RadiusMedium"/>), the inset panels
/// and overlays more so. There are no cut corners in the UI; the angular shape stays in the logo art only.
/// </para>
/// <para>
/// Reusable classes: <c>panel</c> (an inset, rounded surface on the shell), <c>card</c> (a raised
/// surface inside a view), <c>kbd</c> (a key cap), <c>section-title</c> and <c>title</c>/<c>heading</c>
/// (the type scale's header steps), <c>secondary</c>/<c>muted</c> (supporting text).
/// </para>
/// </summary>
public static class AppStyles
{
    /// <summary>Container theme for list rows (explorer, palette): rounded, inset rows in token colors.</summary>
    public static readonly ControlTheme ListRowTheme = CreateListRowTheme();

    /// <summary>The focused-field halo: rebuilt on theme changes, since a shadow takes a color, not a brush.</summary>
    private static Styles? _focusRing;

    public static void Apply(Application app)
    {
        app.Resources[typeof(ScrollBar)] = ScrollBarTheme.Create(); // every scroll bar, including Fluent's ScrollViewer

        app.Styles.AddRange(
        [
            // Strips that scroll sideways (editor tabs) get the slim scroll bar over their bottom edge.
            Rule(x => x.OfType<ScrollViewer>().Class("slim").Template().OfType<ScrollBar>(),
                (StyledElement.ThemeProperty, ScrollBarTheme.Slim)),

            // The window is the shell surface; panels sit on it.
            Rule<Window>(
                (TemplatedControl.BackgroundProperty, Palette.Chrome),
                (TemplatedControl.FontFamilyProperty, Typography.Ui),
                (TemplatedControl.FontSizeProperty, Typography.UiSize),
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary)),

            // Surfaces.
            Rule(x => x.OfType<Border>().Class("panel"),
                (Border.BackgroundProperty, Palette.EditorBackground),
                (Border.BorderBrushProperty, Palette.Border),
                (Border.BorderThicknessProperty, new Thickness(1)),
                (Border.CornerRadiusProperty, Spacing.RadiusPanel),
                (Visual.ClipToBoundsProperty, true)),
            Rule(x => x.OfType<Border>().Class("card"),
                (Border.BackgroundProperty, Palette.Elevated),
                (Border.BorderBrushProperty, Palette.BorderStrong),
                (Border.BorderThicknessProperty, new Thickness(1)),
                (Border.CornerRadiusProperty, Spacing.RadiusLarge)),
            Rule(x => x.OfType<Border>().Class("kbd"),
                (Border.BackgroundProperty, Palette.Elevated),
                (Border.BorderBrushProperty, Palette.BorderStrong),
                (Border.BorderThicknessProperty, new Thickness(1, 1, 1, 2)),
                (Border.CornerRadiusProperty, Spacing.RadiusSmall),
                (Decorator.PaddingProperty, new Thickness(5, 0))),

            // Type scale steps (see Typography).
            Rule(x => x.OfType<TextBlock>().Class("section-title"),
                (TextBlock.FontSizeProperty, Typography.SmallSize),
                (TextBlock.FontWeightProperty, Typography.Strong),
                (TextBlock.ForegroundProperty, Palette.TextSecondary)),
            Rule(x => x.OfType<TextBlock>().Class("title"),
                (TextBlock.FontSizeProperty, Typography.TitleSize),
                (TextBlock.FontWeightProperty, Typography.Strong),
                (TextBlock.ForegroundProperty, Palette.TextPrimary)),
            Rule(x => x.OfType<TextBlock>().Class("heading"),
                (TextBlock.FontSizeProperty, Typography.HeadingSize),
                (TextBlock.FontWeightProperty, Typography.Strong),
                (TextBlock.ForegroundProperty, Palette.TextPrimary)),
            Rule(x => x.OfType<TextBlock>().Class("secondary"), (TextBlock.ForegroundProperty, Palette.TextSecondary)),
            Rule(x => x.OfType<TextBlock>().Class("muted"), (TextBlock.ForegroundProperty, Palette.TextMuted)),

            // Flat buttons. Clickable things show the hand (pointer) cursor.
            Rule<FlatButton>((InputElement.CursorProperty, new Cursor(StandardCursorType.Hand))),
            Rule<FlatButton>(
                (TemplatedControl.BackgroundProperty, Palette.HoverClear),
                (TemplatedControl.BorderBrushProperty, Brushes.Transparent),
                (TemplatedControl.ForegroundProperty, Palette.TextSecondary),
                (FlatButton.ChamferProperty, 0.0),
                (FlatButton.RadiusProperty, Spacing.RadiusMedium.TopLeft),
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

            // Activity bar tiles: the open view's tile is tinted with the accent (its edge pill adds a shape cue).
            Rule(x => x.OfType<FlatButton>().Class("activity").Class("active"),
                (TemplatedControl.BackgroundProperty, Palette.Selected),
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary)),

            // View switchers (panel tabs, settings sections): the active one is a filled pill.
            Rule(x => x.OfType<FlatButton>().Class("panel-tab").Class("active"),
                (TemplatedControl.BackgroundProperty, Palette.Pressed),
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary)),

            // Primary call-to-action: the solid accent fill, rounded like every other control.
            Rule(x => x.OfType<FlatButton>().Class("primary"),
                (TemplatedControl.BackgroundProperty, Palette.AccentFill),
                (TemplatedControl.ForegroundProperty, Palette.TextOnAccent),
                (TemplatedControl.FontWeightProperty, Typography.Strong),
                (TemplatedControl.PaddingProperty, new Thickness(16, 7)),
                (FlatButton.RadiusProperty, Spacing.RadiusMedium.TopLeft)),
            Rule(x => x.OfType<FlatButton>().Class("primary").Class(":pointerover"),
                (TemplatedControl.BackgroundProperty, Palette.AccentFillHover),
                (TemplatedControl.ForegroundProperty, Palette.TextOnAccent)),
            Rule(x => x.OfType<FlatButton>().Class("primary").Class(":focus-visible"),
                (TemplatedControl.BorderBrushProperty, Palette.TextOnAccent)),

            // Secondary button: an outlined, quieter sibling of primary.
            Rule(x => x.OfType<FlatButton>().Class("secondary"),
                (TemplatedControl.BorderBrushProperty, Palette.BorderStrong),
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary),
                (TemplatedControl.PaddingProperty, new Thickness(14, 6))),

            // Title bar command center: a field-like button that opens quick-open.
            Rule(x => x.OfType<FlatButton>().Class("command-center"),
                (TemplatedControl.BackgroundProperty, Palette.Elevated),
                (TemplatedControl.BorderBrushProperty, Palette.BorderStrong)),
            Rule(x => x.OfType<FlatButton>().Class("command-center").Class(":pointerover"),
                (TemplatedControl.BackgroundProperty, Palette.Hover),
                (TemplatedControl.BorderBrushProperty, Palette.InputBorder)),

            // Window caption buttons are square; close turns red like the platform convention.
            Rule(x => x.OfType<FlatButton>().Class("caption"),
                (FlatButton.RadiusProperty, 0.0),
                (TemplatedControl.BorderBrushProperty, Brushes.Transparent)),
            Rule(x => x.OfType<FlatButton>().Class("caption").Class("close").Class(":pointerover"),
                (TemplatedControl.BackgroundProperty, Palette.Danger),
                (TemplatedControl.ForegroundProperty, Brushes.White)),

            // Toggle buttons (find options): small radius, and an "on" state that does not rely on color alone.
            Rule(x => x.OfType<FlatButton>().Class("toggle"),
                (FlatButton.RadiusProperty, Spacing.RadiusSmall.TopLeft)),
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

            // Rows with hover actions: the actions take the detail's place on hover or keyboard selection.
            Rule(x => x.OfType<StackPanel>().Class("row-actions"), (Visual.IsVisibleProperty, false)),
            Rule(x => Selectors.Or(
                    x.OfType<ListBoxItem>().Class(":pointerover").Descendant().OfType<StackPanel>().Class("row-actions"),
                    x.OfType<ListBoxItem>().Class(":selected").Descendant().OfType<StackPanel>().Class("row-actions")),
                (Visual.IsVisibleProperty, true)),
            Rule(x => Selectors.Or(
                    x.OfType<ListBoxItem>().Class(":pointerover").Descendant().OfType<TextBlock>().Class("row-detail"),
                    x.OfType<ListBoxItem>().Class(":selected").Descendant().OfType<TextBlock>().Class("row-detail")),
                (Visual.IsVisibleProperty, false)),

            // The native frame stays (Windows animates it, snaps it, and draws its border); the title bar and
            // caption buttons are Coder's own, so Avalonia draws none over them.
            Rule(x => x.OfType<Avalonia.Controls.Chrome.WindowDrawnDecorations>(), (Avalonia.Controls.Chrome.WindowDrawnDecorations.TemplateProperty, new NoDrawnDecorations())),

            // Context menus: an overlay surface, rounded like the palette.
            Rule<ContextMenu>(
                (TemplatedControl.BackgroundProperty, Palette.Elevated),
                (TemplatedControl.BorderBrushProperty, Palette.BorderStrong),
                (TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                (TemplatedControl.CornerRadiusProperty, Spacing.RadiusLarge),
                (TemplatedControl.PaddingProperty, new Thickness(4))),
            Rule<MenuItem>(
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary),
                (TemplatedControl.FontSizeProperty, Typography.UiSize),
                (TemplatedControl.CornerRadiusProperty, Spacing.RadiusMedium),
                (TemplatedControl.PaddingProperty, new Thickness(10, 5))),
            Rule(x => Selectors.Or(x.OfType<MenuItem>().Class(":pointerover"), x.OfType<MenuItem>().Class(":selected")),
                (TemplatedControl.BackgroundProperty, Palette.Selected)),
            Rule(x => x.OfType<MenuItem>().Class("accent"), (TemplatedControl.ForegroundProperty, Palette.Accent), (TemplatedControl.FontWeightProperty, Typography.Strong)),
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

            // Text boxes: a field one step above its surface that gains an accent border and halo when focused.
            Rule<TextBox>(
                (TemplatedControl.BackgroundProperty, Palette.Elevated),
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary),
                (TemplatedControl.BorderBrushProperty, Palette.InputBorder),
                (TemplatedControl.CornerRadiusProperty, Spacing.RadiusMedium),
                (TextBox.SelectionBrushProperty, Palette.TextSelection),
                (TextBox.CaretBrushProperty, Palette.Caret)),
            Rule(x => x.OfType<TextBox>().Class(":pointerover").Template().OfType<Border>().Name("PART_BorderElement"),
                (Border.BackgroundProperty, Palette.Elevated),
                (Border.BorderBrushProperty, Palette.BorderStrong)),
            Rule(x => x.OfType<TextBox>().Class(":focus").Template().OfType<Border>().Name("PART_BorderElement"),
                (Border.BackgroundProperty, Palette.Elevated),
                (Border.BorderBrushProperty, Palette.Accent),
                (Border.BorderThicknessProperty, new Thickness(1))),

            // Dropdowns: the same field as a text box, a muted chevron, and an overlay list with rounded rows.
            Rule<ComboBox>(
                (TemplatedControl.BackgroundProperty, Palette.Elevated),
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary),
                (TemplatedControl.BorderBrushProperty, Palette.InputBorder),
                (TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                (TemplatedControl.CornerRadiusProperty, Spacing.RadiusMedium),
                (TemplatedControl.PaddingProperty, new Thickness(Spacing.M - 2, 5, Spacing.S, 5)),
                (Layoutable.MinHeightProperty, 30.0),
                (InputElement.CursorProperty, new Cursor(StandardCursorType.Hand))),
            Rule(x => x.OfType<ComboBox>().Template().OfType<Border>().Name("Background"),
                (Border.BackgroundProperty, Palette.Elevated),
                (Border.BorderBrushProperty, Palette.InputBorder)),
            Rule(x => x.OfType<ComboBox>().Class(":pointerover").Template().OfType<Border>().Name("Background"),
                (Border.BackgroundProperty, Palette.Hover),
                (Border.BorderBrushProperty, Palette.BorderStrong)),
            Rule(x => Selectors.Or(
                    x.OfType<ComboBox>().Class(":focus-visible").Template().OfType<Border>().Name("Background"),
                    x.OfType<ComboBox>().Class(":dropdownopen").Template().OfType<Border>().Name("Background")),
                (Border.BorderBrushProperty, Palette.Accent)),
            Rule(x => x.OfType<ComboBox>().Template().OfType<PathIcon>().Name("DropDownGlyph"),
                (PathIcon.ForegroundProperty, Palette.TextSecondary)),
            Rule(x => x.OfType<ComboBox>().Template().OfType<Border>().Name("PopupBorder"),
                (Border.BackgroundProperty, Palette.Elevated),
                (Border.BorderBrushProperty, Palette.BorderStrong),
                (Border.BorderThicknessProperty, new Thickness(1)),
                (Border.CornerRadiusProperty, Spacing.RadiusLarge),
                (Border.PaddingProperty, new Thickness(Spacing.XS))),
            Rule<ComboBoxItem>(
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary),
                (TemplatedControl.CornerRadiusProperty, Spacing.RadiusMedium),
                (TemplatedControl.PaddingProperty, new Thickness(Spacing.S + Spacing.XXS, 6)),
                (InputElement.CursorProperty, new Cursor(StandardCursorType.Hand))),
            Rule(x => x.OfType<ComboBoxItem>().Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                (ContentPresenter.BackgroundProperty, Brushes.Transparent),
                (ContentPresenter.ForegroundProperty, Palette.TextPrimary),
                (ContentPresenter.CornerRadiusProperty, Spacing.RadiusMedium)),
            Rule(x => x.OfType<ComboBoxItem>().Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                (ContentPresenter.BackgroundProperty, Palette.Hover),
                (ContentPresenter.ForegroundProperty, Palette.TextPrimary)),
            Rule(x => Selectors.Or(
                    x.OfType<ComboBoxItem>().Class(":selected").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                    x.OfType<ComboBoxItem>().Class(":selected").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter")),
                (ContentPresenter.BackgroundProperty, Palette.Selected),
                (ContentPresenter.ForegroundProperty, Palette.TextPrimary)),

            // The app's number field (Controls/NumberBox): the same field as a text box, chevrons on the right.
            Rule(x => x.OfType<NumberBox>(),
                (Border.BackgroundProperty, Palette.Elevated),
                (Border.BorderBrushProperty, Palette.InputBorder),
                (Border.BorderThicknessProperty, new Thickness(1)),
                (Border.CornerRadiusProperty, Spacing.RadiusMedium),
                (Layoutable.MinHeightProperty, 30.0)),
            Rule(x => x.OfType<NumberBox>().Class(":pointerover"),
                (Border.BorderBrushProperty, Palette.BorderStrong)),
            Rule(x => x.OfType<NumberBox>().Class(":focus-within"),
                (Border.BorderBrushProperty, Palette.Accent)),

            // Number boxes: one field (the spinner's border) with flat step buttons and a borderless text inside.
            Rule<NumericUpDown>(
                (TemplatedControl.ForegroundProperty, Palette.TextPrimary),
                (TemplatedControl.CornerRadiusProperty, Spacing.RadiusMedium),
                (Layoutable.MinHeightProperty, 30.0)),
            Rule(x => x.OfType<NumericUpDown>().Template().OfType<ButtonSpinner>(),
                (TemplatedControl.BackgroundProperty, Palette.Elevated),
                (TemplatedControl.BorderBrushProperty, Palette.InputBorder),
                (TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                (TemplatedControl.CornerRadiusProperty, Spacing.RadiusMedium)),
            Rule(x => x.OfType<NumericUpDown>().Class(":pointerover").Template().OfType<ButtonSpinner>(),
                (TemplatedControl.BorderBrushProperty, Palette.BorderStrong)),
            Rule(x => x.OfType<NumericUpDown>().Class(":focus-within").Template().OfType<ButtonSpinner>(),
                (TemplatedControl.BorderBrushProperty, Palette.Accent)),
            Rule(x => x.OfType<ButtonSpinner>().Template().OfType<RepeatButton>(),
                (TemplatedControl.BackgroundProperty, Brushes.Transparent),
                (TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                (TemplatedControl.ForegroundProperty, Palette.TextSecondary),
                (TemplatedControl.CornerRadiusProperty, Spacing.RadiusSmall),
                (Layoutable.MarginProperty, new Thickness(1)),
                (InputElement.CursorProperty, new Cursor(StandardCursorType.Hand))),
            Rule(x => x.OfType<ButtonSpinner>().Template().OfType<RepeatButton>().Class(":pointerover").Template().OfType<ContentPresenter>(),
                (ContentPresenter.BackgroundProperty, Palette.Hover),
                (ContentPresenter.ForegroundProperty, Palette.TextPrimary)),
            Rule(x => x.OfType<ButtonSpinner>().Template().OfType<RepeatButton>().Class(":pressed").Template().OfType<ContentPresenter>(),
                (ContentPresenter.BackgroundProperty, Palette.Pressed)),

            // Switches: an outlined track with a muted knob when off; a solid accent track and white knob when on.
            Rule<ToggleSwitch>((InputElement.CursorProperty, new Cursor(StandardCursorType.Hand))),
            Rule(x => x.OfType<ToggleSwitch>().Template().OfType<Border>().Name("OuterBorder"),
                (Border.BackgroundProperty, Palette.Elevated),
                (Border.BorderBrushProperty, Palette.InputBorder)),
            Rule(x => x.OfType<ToggleSwitch>().Class(":pointerover").Template().OfType<Border>().Name("OuterBorder"),
                (Border.BackgroundProperty, Palette.Hover),
                (Border.BorderBrushProperty, Palette.TextSecondary)),
            Rule(x => x.OfType<ToggleSwitch>().Template().OfType<Avalonia.Controls.Shapes.Ellipse>().Name("SwitchKnobOff"),
                (Avalonia.Controls.Shapes.Shape.FillProperty, Palette.TextSecondary)),
            Rule(x => x.OfType<ToggleSwitch>().Template().OfType<Border>().Name("SwitchKnobBounds"),
                (Border.BackgroundProperty, Palette.AccentFill),
                (Border.BorderBrushProperty, Palette.AccentFill)),
            Rule(x => x.OfType<ToggleSwitch>().Class(":pointerover").Template().OfType<Border>().Name("SwitchKnobBounds"),
                (Border.BackgroundProperty, Palette.AccentFillHover),
                (Border.BorderBrushProperty, Palette.AccentFillHover)),
            Rule(x => x.OfType<ToggleSwitch>().Template().OfType<Avalonia.Controls.Shapes.Ellipse>().Name("SwitchKnobOn"),
                (Avalonia.Controls.Shapes.Shape.FillProperty, Palette.TextOnAccent)),
            Rule(x => x.OfType<ToggleSwitch>().Class(":focus-visible").Template().OfType<Border>().Name("OuterBorder"),
                (Border.BorderBrushProperty, Palette.Accent)),

            // Check boxes and radio buttons, wherever they appear: token colors, accent when checked.
            Rule<CheckBox>((TemplatedControl.ForegroundProperty, Palette.TextPrimary)),
            Rule(x => x.OfType<CheckBox>().Template().OfType<Border>().Name("NormalRectangle"),
                (Border.BackgroundProperty, Palette.Elevated),
                (Border.BorderBrushProperty, Palette.InputBorder),
                (Border.CornerRadiusProperty, Spacing.RadiusSmall)),
            Rule(x => x.OfType<CheckBox>().Class(":checked").Template().OfType<Border>().Name("NormalRectangle"),
                (Border.BackgroundProperty, Palette.AccentFill),
                (Border.BorderBrushProperty, Palette.AccentFill)),
            Rule<RadioButton>((TemplatedControl.ForegroundProperty, Palette.TextPrimary)),

            // Splitters live in the gaps between panels: invisible until hovered, then a faint accent.
            Rule<GridSplitter>((TemplatedControl.BackgroundProperty, Brushes.Transparent)),
            Rule(x => x.OfType<GridSplitter>().Class(":pointerover"), (TemplatedControl.BackgroundProperty, Palette.FocusRing)),
        ]);

        ApplyFocusRing(app);
        ThemeManager.Changed += (_, _) => ApplyFocusRing(app);
    }

    /// <summary>A soft halo outside a focused field's accent border, in the current theme's color.</summary>
    private static void ApplyFocusRing(Application app)
    {
        if (_focusRing is not null)
        {
            app.Styles.Remove(_focusRing);
        }

        var ring = Palette.FocusRing;
        var color = Color.FromArgb((byte)Math.Round(ring.Opacity * ring.Color.A), ring.Color.R, ring.Color.G, ring.Color.B);
        var halo = new BoxShadows(new BoxShadow { Spread = 3, Color = color });

        // Rebuilt as one group so the field overrides below always come after the generic text box halo.
        _focusRing = new Styles
        {
            Rule(x => x.OfType<TextBox>().Class(":focus").Template().OfType<Border>().Name("PART_BorderElement"),
                (Border.BoxShadowProperty, halo)),

            // A number box's text sits inside the spinner field, which draws the border and halo instead.
            Rule(x => x.OfType<NumericUpDown>().Template().OfType<TextBox>().Template().OfType<Border>().Name("PART_BorderElement"),
                (Border.BackgroundProperty, Brushes.Transparent),
                (Border.BorderThicknessProperty, new Thickness(0)),
                (Border.BoxShadowProperty, default(BoxShadows))),
            Rule(x => x.OfType<NumericUpDown>().Template().OfType<TextBox>(),
                (TemplatedControl.BackgroundProperty, Brushes.Transparent),
                (TemplatedControl.BorderThicknessProperty, new Thickness(0))),
            Rule(x => x.OfType<NumericUpDown>().Class(":focus-within").Template().OfType<ButtonSpinner>(),
                (Border.BoxShadowProperty, halo)),

            // Likewise the app's number field: its border carries focus, the text inside has none.
            Rule(x => x.OfType<NumberBox>().Descendant().OfType<TextBox>(),
                (TemplatedControl.BackgroundProperty, Brushes.Transparent),
                (TemplatedControl.BorderThicknessProperty, new Thickness(0))),
            Rule(x => x.OfType<NumberBox>().Descendant().OfType<TextBox>().Template().OfType<Border>().Name("PART_BorderElement"),
                (Border.BackgroundProperty, Brushes.Transparent),
                (Border.BorderThicknessProperty, new Thickness(0)),
                (Border.BoxShadowProperty, default(BoxShadows))),
            Rule(x => x.OfType<NumberBox>().Class(":focus-within"),
                (Border.BoxShadowProperty, halo)),
            Rule(x => Selectors.Or(
                    x.OfType<ComboBox>().Class(":focus-visible").Template().OfType<Border>().Name("Background"),
                    x.OfType<ComboBox>().Class(":dropdownopen").Template().OfType<Border>().Name("Background")),
                (Border.BoxShadowProperty, halo)),
        };
        app.Styles.Add(_focusRing);
    }

    private static ControlTheme CreateListRowTheme()
    {
        var template = new FuncControlTemplate<ListBoxItem>((item, _) => new Border
        {
            Name = "PART_Root",
            [!Border.BackgroundProperty] = item[!TemplatedControl.BackgroundProperty],
            [!Border.BorderBrushProperty] = item[!TemplatedControl.BorderBrushProperty],
            [!Border.CornerRadiusProperty] = item[!TemplatedControl.CornerRadiusProperty],
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
                new Setter(TemplatedControl.CornerRadiusProperty, Spacing.RadiusMedium),
                new Setter(Layoutable.MarginProperty, new Thickness(Spacing.XS, 0)),
                new Setter(Layoutable.MinHeightProperty, Spacing.ListRowHeight),
                new Setter(InputElement.CursorProperty, new Cursor(StandardCursorType.Hand)),
            },
            Children =
            {
                Rule(x => x.Nesting().Class(":pointerover"), (TemplatedControl.BackgroundProperty, Palette.Hover)),
                Rule(x => x.Nesting().Class(":selected"), (TemplatedControl.BackgroundProperty, Palette.SelectedInactive)),
                Rule(x => x.Nesting().Class("drop-inside"), (TemplatedControl.BackgroundProperty, Palette.Hover)),
                Rule(x => x.Nesting().Class("dragging"), (Visual.OpacityProperty, 0.45)),
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
