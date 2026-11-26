using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Coder.App.Theme;

namespace Coder.App.Editor;

/// <summary>
/// Ctrl+hover, like VS Code: with Ctrl held, the symbol under the pointer is underlined and the pointer
/// becomes a hand, showing that Ctrl+Click goes to its definition. Only where a language server can.
/// </summary>
public sealed partial class EditorView
{
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private (int Start, int End)? _ctrlLink;
    private Point? _pointer;

    /// <summary>Whether the shown document's server can go to definitions (so Ctrl+hover offers it).</summary>
    public Func<bool> CanGoToDefinition { get; set; } = () => false;

    /// <summary>For tests: the symbol underlined for Ctrl+Click.</summary>
    internal (int Start, int End)? CtrlLink => _ctrlLink;

    /// <summary>Underlines the symbol under the pointer while Ctrl is held; returns whether one is.</summary>
    private bool UpdateCtrlLink(Point? pointer, KeyModifiers modifiers)
    {
        (int Start, int End)? link = null;
        if (pointer is { } point && modifiers.HasFlag(KeyModifiers.Control) && _editor is { } editor
            && point.X >= GutterWidth && CanGoToDefinition())
        {
            var offset = OffsetAt(point);
            var document = editor.Document;
            if (offset < document.Length && IsIdentifierChar(document[offset]))
            {
                var (start, end) = editor.WordRangeAt(offset);
                link = end > start ? (start, end) : null;
            }
        }

        if (link != _ctrlLink)
        {
            _ctrlLink = link;
            InvalidateVisual();
        }

        if (link is not null)
        {
            Cursor = HandCursor;
        }

        return link is not null;
    }

    private void DrawCtrlLink(DrawingContext context)
    {
        if (_ctrlLink is not { } link || _editor is null)
        {
            return;
        }

        var document = _editor.Document;
        if (link.End > document.Length)
        {
            _ctrlLink = null; // found in a document that is no longer shown (Ctrl+Click opened another file)
            return;
        }

        var start = document.GetPosition(link.Start);
        var end = document.GetPosition(link.End);
        var y = Math.Round(LineTop(start.Line) + _metrics.LineHeight - 3) + 0.5;
        foreach (var (left, right) in SpansOf(start.Line, start.Column, end.Column))
        {
            context.DrawLine(new Pen(Palette.LinkUnderline, 1), new Point(left, y), new Point(right, y));
        }
    }

    /// <summary>The underline belongs to the text it was found in: a new document or an edit drops it.</summary>
    private void ClearCtrlLink() => _ctrlLink = null;

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
