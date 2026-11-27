using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Coder.App.Theme;
using Coder.Core.Lsp;
using Coder.Presentation.LanguageFeatures;

namespace Coder.App.Editor;

/// <summary>
/// The suggestion list under the caret. Rows are drawn directly (kind badge, label with the typed
/// characters highlighted, detail on the right), so showing and filtering hundreds of items costs
/// only the ten visible rows. Keyboard navigation is routed here by the editor area; the mouse can
/// pick (click) and accept (double-click) a row.
/// </summary>
public sealed class CompletionWidget : Decorator
{
    public const double RowHeight = 24;
    public const int VisibleRows = 10;
    public const double ListWidth = 440;

    /// <summary>Horizontal distance from the widget's edge to the label text, so labels line up with the typed word.</summary>
    public const double LabelInset = 1 + Spacing.XS + BadgeLeft + BadgeSize + 8;

    private const double BadgeSize = 16;

    /// <summary>The kind badge's distance from the row's left edge.</summary>
    private const double BadgeLeft = Spacing.XS;

    public CompletionWidget(CompletionViewModel completion)
    {
        var rows = new RowList(completion);
        Width = ListWidth;
        var card = new Border
        {
            Background = Palette.Elevated,
            BorderBrush = Palette.BorderStrong,
            BorderThickness = new Thickness(1),
            CornerRadius = Spacing.RadiusLarge,
            Padding = new Thickness(Spacing.XS, 0), // rows inset from the rounded sides; the height stays rows + border
            Child = rows,
        };
        ThemeManager.BindShadow(card, "0 8 24 0");
        Child = card;
        IsVisible = false;
        completion.Changed += (_, _) =>
        {
            Height = Math.Min(completion.Rows.Count, VisibleRows) * RowHeight + 2;
            rows.Reveal();
            rows.InvalidateVisual();
        };
    }

    /// <summary>Draws the rows and handles the mouse.</summary>
    private sealed class RowList(CompletionViewModel completion) : Control
    {
        private int _firstRow;

        /// <summary>Scrolls just enough to show the selected row.</summary>
        public void Reveal()
        {
            var selected = completion.SelectedIndex;
            if (selected < _firstRow)
            {
                _firstRow = selected;
            }
            else if (selected >= _firstRow + VisibleRows)
            {
                _firstRow = selected - VisibleRows + 1;
            }

            _firstRow = Math.Clamp(_firstRow, 0, Math.Max(0, completion.Rows.Count - VisibleRows));
        }

        public override void Render(DrawingContext context)
        {
            var rows = completion.Rows;
            var width = Bounds.Width;
            for (var i = _firstRow; i < Math.Min(rows.Count, _firstRow + VisibleRows); i++)
            {
                var top = (i - _firstRow) * RowHeight;
                if (i == completion.SelectedIndex)
                {
                    context.DrawRectangle(Palette.Selected, new Pen(Palette.Accent, 1), new Rect(0.5, top + 0.5, width - 1, RowHeight - 1), Spacing.RadiusSmall.TopLeft, Spacing.RadiusSmall.TopLeft);
                }

                DrawRow(context, rows[i], top, width, i == completion.SelectedIndex);
            }
        }

        private static void DrawRow(DrawingContext context, CompletionRow row, double top, double width, bool selected)
        {
            var (glyph, kindBrush) = KindStyle(row.Item.Kind);
            var badge = new Rect(BadgeLeft, top + (RowHeight - BadgeSize) / 2, BadgeSize, BadgeSize);
            context.DrawRectangle(new SolidColorBrush(kindBrush.Color, 0.16), null, badge, 3, 3);
            var badgeText = Text(glyph, 10.5, kindBrush, FontWeight.Bold, Typography.Ui);
            context.DrawText(badgeText, new Point(badge.X + (BadgeSize - badgeText.Width) / 2, badge.Y + (BadgeSize - badgeText.Height) / 2));

            var labelLeft = LabelInset - 1 - Spacing.XS;
            var label = Text(row.Item.Label, 13, Palette.TextPrimary, FontWeight.Normal, Typography.Code);
            foreach (var index in row.MatchedIndices.Where(i => i < row.Item.Label.Length))
            {
                label.SetForegroundBrush(Palette.AccentBright, index, 1);
                label.SetFontWeight(FontWeight.SemiBold, index, 1);
            }

            var detailSpace = width - labelLeft - label.Width - 24;
            label.MaxTextWidth = Math.Max(40, width - labelLeft - 12);
            label.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(label, new Point(labelLeft, top + (RowHeight - label.Height) / 2));

            if (row.Item.Detail is { Length: > 0 } detail && detailSpace > 40)
            {
                var detailText = Text(detail.ReplaceLineEndings(" "), Typography.SmallSize, selected ? Palette.TextSecondary : Palette.TextMuted, FontWeight.Normal, Typography.Ui);
                detailText.MaxTextWidth = detailSpace;
                detailText.Trimming = TextTrimming.CharacterEllipsis;
                detailText.TextAlignment = TextAlignment.Right;
                context.DrawText(detailText, new Point(width - Spacing.S - detailSpace, top + (RowHeight - detailText.Height) / 2));
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var index = _firstRow + (int)(e.GetPosition(this).Y / RowHeight);
            completion.Select(index);
            if (e.ClickCount >= 2)
            {
                completion.Accept();
            }

            e.Handled = true; // keep focus (and the caret) in the editor
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            _firstRow = Math.Clamp(_firstRow - (int)Math.Round(e.Delta.Y * 3), 0, Math.Max(0, completion.Rows.Count - VisibleRows));
            InvalidateVisual();
            e.Handled = true;
        }

        private static FormattedText Text(string text, double size, IBrush brush, FontWeight weight, FontFamily family) =>
            new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, weight), size, brush);

        /// <summary>A one-letter badge and color per kind, grouped the way people scan: callables, values, types, words.</summary>
        private static (string Glyph, SolidColorBrush Brush) KindStyle(CompletionItemKind kind) => kind switch
        {
            CompletionItemKind.Method or CompletionItemKind.Function or CompletionItemKind.Constructor => ("ƒ", Palette.KindFunction),
            CompletionItemKind.Field or CompletionItemKind.Property => ("◆", Palette.KindField),
            CompletionItemKind.Variable or CompletionItemKind.Value or CompletionItemKind.Reference => ("x", Palette.KindVariable),
            CompletionItemKind.Class => ("C", Palette.KindClass),
            CompletionItemKind.Struct => ("S", Palette.KindClass),
            CompletionItemKind.Interface => ("I", Palette.KindInterface),
            CompletionItemKind.TypeParameter => ("T", Palette.KindInterface),
            CompletionItemKind.Enum => ("E", Palette.KindEnum),
            CompletionItemKind.EnumMember or CompletionItemKind.Constant => ("π", Palette.KindEnum),
            CompletionItemKind.Module or CompletionItemKind.Folder or CompletionItemKind.File => ("{}", Palette.KindModule),
            CompletionItemKind.Keyword or CompletionItemKind.Operator => ("k", Palette.KindKeyword),
            CompletionItemKind.Snippet => ("…", Palette.KindSnippet),
            CompletionItemKind.Event => ("e", Palette.KindEvent),
            _ => ("a", Palette.KindKeyword),
        };
    }
}
