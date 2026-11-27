using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Coder.App.Theme;

namespace Coder.App.Editor;

/// <summary>
/// Inline blame: who last changed the caret's line, in faint italics after its end, like VS Code
/// ("You, 2 days ago • Fix the parser"). Shown only with a single caret and no selection.
/// </summary>
public sealed partial class EditorView
{
    private const int BlameGapColumns = 4;

    private Func<int, string?> _blame = _ => null;
    private (string Text, double FontSize, TextLayout Layout)? _blameLayout;

    /// <summary>The annotation for a line, or null (read on every repaint).</summary>
    public void SetBlameSource(Func<int, string?> blame)
    {
        _blame = blame;
        InvalidateVisual();
    }

    /// <summary>For tests: the annotation shown now, if any.</summary>
    internal string? ShownBlame => _editor is { } editor && editor.Selections.All.Count == 1 && editor.Selection.IsEmpty && CurrentSuggestion() is null
        ? _blame(editor.CaretPosition.Line)
        : null;

    private void DrawBlame(DrawingContext context)
    {
        if (ShownBlame is not { } text)
        {
            return;
        }

        // Shaped once per annotation: the caret blinks (repaints) far more often than the line changes.
        if (_blameLayout is not { } cached || cached.Text != text || cached.FontSize != _metrics.FontSize)
        {
            var layout = new TextLayout(text, new Typeface(Typography.Ui, FontStyle.Italic), _metrics.FontSize * 0.92, Palette.TextMuted);
            _blameLayout = cached = (text, _metrics.FontSize, layout);
        }

        var line = _editor!.CaretPosition.Line;
        var x = LineEndX(line) + BlameGapColumns * _metrics.CharWidth; // after any inlay hint at the line end
        var y = LineTop(line) + (_metrics.LineHeight - cached.Layout.Height) / 2;
        using (context.PushOpacity(0.8))
        {
            cached.Layout.Draw(context, new Point(x, y));
        }
    }
}
