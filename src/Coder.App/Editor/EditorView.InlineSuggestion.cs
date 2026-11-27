using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Coder.App.Theme;
using Coder.Core.Text;

namespace Coder.App.Editor;

/// <summary>
/// AI inline suggestions ("ghost text"): the suggestion's first line is shaped into the caret's line in
/// a faint color, pushing the rest of that line right; its further lines are drawn over the lines below
/// on the editor's background, like a peek, until Tab accepts or anything else drops it. Ghost text is
/// not in the document: the caret stays before it and clicks never land in it.
/// </summary>
public sealed partial class EditorView
{
    private Func<(int Offset, string Text)?> _inlineSuggestion = () => null;
    private int? _suggestionLine;
    private readonly List<(string Text, double FontSize, TextLayout Layout)> _suggestionLayouts = [];

    /// <summary>Where the suggestion to show comes from (read when lines are laid out and drawn).</summary>
    public void SetInlineSuggestionSource(Func<(int Offset, string Text)?> suggestion)
    {
        _inlineSuggestion = suggestion;
        InlineSuggestionChanged();
    }

    /// <summary>The suggestion changed: lay out its line (and the one it left) again.</summary>
    public void InlineSuggestionChanged()
    {
        if (_suggestionLine is { } previous)
        {
            _layouts.Invalidate(previous);
        }

        _suggestionLine = CurrentSuggestion() is { } suggestion ? _editor!.Document.GetLineOfOffset(suggestion.Offset) : null;
        if (_suggestionLine is { } line)
        {
            _layouts.Invalidate(line);
        }

        InvalidateVisual();
    }

    /// <summary>For tests: the suggestion drawn now, if any.</summary>
    internal string? ShownInlineSuggestion => CurrentSuggestion()?.Text;

    /// <summary>The suggestion, when it still fits the document (a stale one is ignored until replaced).</summary>
    private (int Offset, string Text)? CurrentSuggestion() =>
        _editor is { } editor && _inlineSuggestion() is { } suggestion && suggestion.Offset <= editor.Document.Length && suggestion.Text.Length > 0
            ? suggestion
            : null;

    /// <summary>The ghost insertion for <paramref name="line"/>: the suggestion's first line, at the caret.</summary>
    private LineInsertion? SuggestionInsertion(int line, string lineText)
    {
        if (CurrentSuggestion() is not { } suggestion || _editor!.Document.GetLineOfOffset(suggestion.Offset) != line)
        {
            return null;
        }

        var firstLine = suggestion.Text.Split('\n')[0];
        if (firstLine.Length == 0)
        {
            return null; // the suggestion starts on the next line
        }

        var column = suggestion.Offset - _editor.Document.GetLineStart(line);
        return new LineInsertion(DisplayIndex(lineText, Math.Min(column, lineText.Length)), TextColumns.ExpandTabs(firstLine, TabSize), false, false, CaretAfter: false, IsSuggestion: true);
    }

    /// <summary>The suggestion's further lines, on the editor's background over the lines below the caret.</summary>
    private void DrawInlineSuggestionLines(DrawingContext context, double width)
    {
        if (CurrentSuggestion() is not { } suggestion)
        {
            return;
        }

        var lines = suggestion.Text.Split('\n');
        if (lines.Length < 2)
        {
            return;
        }

        var caretLine = _editor!.Document.GetLineOfOffset(suggestion.Offset);
        var left = TextLeft - _offset.X;
        for (var i = 1; i < lines.Length; i++)
        {
            var top = LineTop(caretLine + i);
            if (top > Bounds.Height)
            {
                break;
            }

            if (top + _metrics.LineHeight < 0)
            {
                continue;
            }

            context.FillRectangle(Palette.EditorBackground, new Rect(GutterWidth, top, Math.Max(0, width - GutterWidth), _metrics.LineHeight));
            SuggestionLayout(i - 1, TextColumns.ExpandTabs(lines[i], TabSize)).Draw(context, new Point(left, top + _metrics.TextTop));
        }
    }

    /// <summary>Shaped once per text: the caret blinks (repaints) far more often than the suggestion changes.</summary>
    private TextLayout SuggestionLayout(int index, string text)
    {
        while (_suggestionLayouts.Count <= index)
        {
            _suggestionLayouts.Add((string.Empty, 0, new TextLayout(string.Empty, _metrics.Typeface, _metrics.FontSize, Palette.InlineSuggestion)));
        }

        var cached = _suggestionLayouts[index];
        if (cached.Text != text || cached.FontSize != _metrics.FontSize)
        {
            cached = (text, _metrics.FontSize, new TextLayout(text, _metrics.Typeface, _metrics.FontSize, Palette.InlineSuggestion));
            _suggestionLayouts[index] = cached;
        }

        return cached.Layout;
    }
}
