using Coder.Core.Lsp;
using Coder.Core.Text;

namespace Coder.App.Editor;

/// <summary>
/// Inlay hints: parameter names and inferred types from the language server, shaped into the lines in
/// a faint color on a light background ("Add(count: 2)"). They take space on screen but not in the
/// document: carets, selections and clicks skip over them (see <see cref="LineLayout"/>).
/// </summary>
public sealed partial class EditorView
{
    private Func<int, IReadOnlyList<InlayHint>> _inlayHints = _ => [];

    /// <summary>Where a line's hints come from (read when the line is laid out).</summary>
    public void SetInlayHintsSource(Func<int, IReadOnlyList<InlayHint>> hints)
    {
        _inlayHints = hints;
        InlayHintsChanged();
    }

    /// <summary>The hints were replaced: lay the lines out again.</summary>
    public void InlayHintsChanged()
    {
        _layouts.Clear();
        InvalidateVisual();
    }

    /// <summary>A line's hints (and the inline suggestion on the caret's line) as insertions at display indexes (tabs expanded).</summary>
    private IReadOnlyList<LineInsertion> HintInsertionsOf(int line)
    {
        var hints = _inlayHints(line);
        var isSuggestionLine = _suggestionLine == line;
        if (hints.Count == 0 && !isSuggestionLine)
        {
            return [];
        }

        var text = _editor!.Document.GetLineText(line);
        if (isSuggestionLine && SuggestionInsertion(line, text) is { } suggestion)
        {
            return [.. HintInsertions(hints, text).Append(suggestion).OrderBy(i => i.At)]; // stable: a hint at the caret stays before the ghost text
        }

        return HintInsertions(hints, text);
    }

    private List<LineInsertion> HintInsertions(IReadOnlyList<InlayHint> hints, string text)
    {
        if (hints.Count == 0)
        {
            return [];
        }

        return hints
            .Select(hint => new LineInsertion(
                DisplayIndex(text, Math.Min(hint.Position.Column, text.Length)),
                hint.Label.ReplaceLineEndings(" "),
                hint.PaddingLeft,
                hint.PaddingRight,
                CaretAfter: hint.Kind == InlayHintKind.Parameter)) // a parameter name belongs to the argument after it
            .ToList();
    }
}
