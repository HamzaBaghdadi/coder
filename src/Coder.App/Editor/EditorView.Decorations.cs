using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Coder.App.Theme;
using Coder.Presentation.Editors;

namespace Coder.App.Editor;

/// <summary>
/// End-of-line decorations from extensions (Error Lens's messages): text after a line's end, colored by its
/// kind like a diagnostic, on a faint tint of that color. Only visible lines are drawn; nothing changes the layout.
/// </summary>
public sealed partial class EditorView
{
    private const int DecorationGapColumns = 3;
    private const int MaxCachedDecorationLayouts = 300;

    private readonly Dictionary<(string Text, DecorationKind Kind, double Size), TextLayout> _decorationLayouts = [];
    private Func<int, LineDecoration?> _decorations = _ => null;

    /// <summary>A line's decoration, or null (read on every repaint).</summary>
    public void SetDecorationsSource(Func<int, LineDecoration?> decorations)
    {
        _decorations = decorations;
        InvalidateVisual();
    }

    /// <summary>For tests: the decoration drawn on a line, if any.</summary>
    internal LineDecoration? ShownDecoration(int line) => _editor is null ? null : _decorations(line);

    private void DrawDecorations(DrawingContext context, int firstLine, int lastLine)
    {
        for (var line = firstLine; line <= lastLine; line = NextLine(line))
        {
            if (_decorations(line) is not { } decoration)
            {
                continue;
            }

            var layout = DecorationLayout(decoration);
            var x = LineTailX(line) + DecorationGapColumns * _metrics.CharWidth;
            var top = LineTop(line);
            var tint = new SolidColorBrush(DecorationColor(decoration.Kind).Color, 0.12);
            context.FillRectangle(tint, new Rect(x - _metrics.CharWidth / 2, top + 1, layout.WidthIncludingTrailingWhitespace + _metrics.CharWidth, _metrics.LineHeight - 2), 3);
            layout.Draw(context, new Point(x, top + (_metrics.LineHeight - layout.Height) / 2));
        }
    }

    /// <summary>Where a line's decoration ends (blame goes after it), or the line's end when it has none.</summary>
    private double DecorationEndX(int line) =>
        _decorations(line) is { } decoration
            ? LineTailX(line) + (DecorationGapColumns + 1) * _metrics.CharWidth + DecorationLayout(decoration).WidthIncludingTrailingWhitespace
            : LineTailX(line);

    private TextLayout DecorationLayout(LineDecoration decoration)
    {
        var key = (decoration.Text, decoration.Kind, _metrics.FontSize);
        if (!_decorationLayouts.TryGetValue(key, out var layout))
        {
            if (_decorationLayouts.Count >= MaxCachedDecorationLayouts)
            {
                _decorationLayouts.Clear();
            }

            layout = new TextLayout(decoration.Text, new Typeface(Typography.Ui, FontStyle.Italic), _metrics.FontSize * 0.92, DecorationColor(decoration.Kind));
            _decorationLayouts[key] = layout;
        }

        return layout;
    }

    private static ISolidColorBrush DecorationColor(DecorationKind kind) => kind switch
    {
        DecorationKind.Error => Palette.DiagnosticError,
        DecorationKind.Warning => Palette.DiagnosticWarning,
        DecorationKind.Information => Palette.DiagnosticInfo,
        DecorationKind.Hint => Palette.DiagnosticHint,
        _ => Palette.TextMuted,
    };
}
