using Coder.Core.Syntax;
using Coder.Core.Text;

namespace Coder.Core.Editing;

/// <summary>
/// The editing model behind one editor view: a document plus one or more selections (cursors), and
/// every operation a user can perform on them. It knows nothing about pixels or keys, so it is fully
/// unit-testable; the view maps input to these methods and renders the result.
/// <para>
/// Multiple cursors: each operation is written for one cursor (reading <see cref="Selection"/>) and
/// is run once per cursor by <see cref="RunEdit"/> or <see cref="RunMove"/>. Edits run from the last
/// cursor to the first, so an edit never moves a cursor that has not run yet; cursors that already
/// ran are shifted by every later change. All cursors' edits form one undo step.
/// </para>
/// <para>This file holds selection state, the per-cursor engine and navigation. Typing, line and
/// multi-cursor commands live in the other <c>EditorState.*.cs</c> parts.</para>
/// </summary>
public sealed partial class EditorState
{
    private SelectionSet _selections = SelectionSet.Single(default);

    // Remembered when moving vertically, one per cursor, so each returns to its column after passing
    // short lines. Cleared by any other selection change.
    private IReadOnlyList<int>? _preferredColumns;

    // State of the per-cursor run in progress (see RunEdit / RunMove).
    private bool _inCaretRun;
    private TextDocument.TextEdit? _runEdit;
    private Selection _current;
    private int _caretIndex;

    public EditorState(TextDocument document, LanguageDefinition language, EditorOptions? options = null)
    {
        Document = document;
        Language = language;
        Options = options ?? new EditorOptions();
    }

    public event EventHandler? SelectionChanged;

    public TextDocument Document { get; }

    public LanguageDefinition Language { get; }

    public EditorOptions Options { get; set; }

    /// <summary>All cursors and selections.</summary>
    public SelectionSet Selections => _selections;

    /// <summary>
    /// The selection being worked on: the primary selection, or, while an operation runs for each
    /// cursor, the cursor currently being processed.
    /// </summary>
    public Selection Selection => _inCaretRun ? _current : _selections.Primary;

    public TextPosition CaretPosition => Document.GetPosition(Selection.Active);

    public string SelectedText => Document.GetText(Selection.Start, Selection.Length);

    /// <summary>Replaces all cursors with a single selection.</summary>
    public void SetSelection(Selection selection)
    {
        if (_inCaretRun)
        {
            _current = selection.Clamp(Document.Length);
            return;
        }

        SetSelections(SelectionSet.Single(selection));
    }

    public void SetSelections(SelectionSet selections)
    {
        selections = selections.Clamp(Document.Length);
        _preferredColumns = null;
        if (selections == _selections)
        {
            return;
        }

        _selections = selections;
        Document.SealUndoStep();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Places the caret, or extends the selection to <paramref name="offset"/>.</summary>
    public void SetCaret(int offset, bool extend = false) =>
        SetSelection(extend ? Selection.ExtendTo(offset) : Selection.Caret(offset));

    public void SelectAll() => SetSelection(new Selection(0, Document.Length));

    public void SelectWordAt(int offset)
    {
        var (start, end) = WordRangeAt(offset);
        SetSelection(new Selection(start, end));
    }

    public void SelectLine(int line)
    {
        var start = Document.GetLineStart(line);
        var end = line + 1 < Document.LineCount ? Document.GetLineStart(line + 1) : Document.Length;
        SetSelection(new Selection(start, end));
    }

    /// <summary>Moves every cursor (or extends every selection).</summary>
    public void MoveCaret(CaretMovement movement, bool extend = false)
    {
        if (movement is CaretMovement.Up or CaretMovement.Down)
        {
            MoveCaretByLines(movement == CaretMovement.Up ? -1 : 1, extend);
            return;
        }

        RunMove(() => MoveOne(movement, extend));
    }

    /// <summary>Moves every cursor up (negative) or down (positive) by whole lines, keeping its column.</summary>
    public void MoveCaretByLines(int lineDelta, bool extend = false)
    {
        var carets = _selections.All;
        var preferred = _preferredColumns?.Count == carets.Count
            ? _preferredColumns
            : carets.Select(caret => VisualColumnOf(caret.Active)).ToList();

        var moved = carets.Select((caret, i) => MoveByLines(caret, lineDelta, extend, preferred[i])).ToList();
        SetSelections(SelectionSet.Create(moved, moved[_selections.PrimaryIndex]));
        _preferredColumns = _selections.Count == preferred.Count ? preferred : null;
    }

    /// <summary>The word (or run of punctuation/whitespace) around <paramref name="offset"/>.</summary>
    public (int Start, int End) WordRangeAt(int offset)
    {
        var line = Document.GetLineOfOffset(offset);
        var lineStart = Document.GetLineStart(line);
        var lineEnd = Document.GetLineEnd(line);
        if (lineStart == lineEnd)
        {
            return (offset, offset);
        }

        // Prefer the character after the caret; at line end use the one before it.
        var probe = offset < lineEnd ? offset : offset - 1;
        var kind = CharClassifier.Classify(Document[probe]);
        int start = probe, end = probe + 1;
        while (start > lineStart && CharClassifier.Classify(Document[start - 1]) == kind)
        {
            start--;
        }

        while (end < lineEnd && CharClassifier.Classify(Document[end]) == kind)
        {
            end++;
        }

        return (start, end);
    }

    /// <summary>The first and last line touched by the selection (a trailing column-0 end is excluded).</summary>
    public (int First, int Last) SelectedLines() => LinesOf(Selection);

    private (int First, int Last) LinesOf(Selection selection)
    {
        var first = Document.GetLineOfOffset(selection.Start);
        var last = Document.GetLineOfOffset(selection.End);
        if (last > first && Document.GetLineStart(last) == selection.End)
        {
            last--;
        }

        return (first, last);
    }

    // Per-cursor engine ---------------------------------------------------------------------------

    /// <summary>Runs a cursor movement once per cursor; the results are merged.</summary>
    private void RunMove(Action atCaret)
    {
        var results = new List<Selection>(_selections.Count);
        _inCaretRun = true;
        try
        {
            foreach (var caret in _selections.All)
            {
                _current = caret;
                atCaret();
                results.Add(_current);
            }
        }
        finally
        {
            _inCaretRun = false;
        }

        SetSelections(SelectionSet.Create(results, results[_selections.PrimaryIndex]));
    }

    /// <summary>Runs an edit once per cursor, as a single undo step.</summary>
    private void RunEdit(EditKind kind, Action atCaret) =>
        EditGroups(kind, _selections.All.Select(caret => new CaretGroup([caret])).ToList(), (edit, group) =>
        {
            _runEdit = edit;
            _current = group.Selections[0];
            atCaret();
            return [_current];
        });

    /// <summary>
    /// Applies an edit to groups of cursors as one undo step. Groups are processed from the end of the
    /// document backwards; results already produced are shifted by each later change. Each call of
    /// <paramref name="apply"/> returns the new selection for every cursor of its group, in order.
    /// </summary>
    private void EditGroups(EditKind kind, IReadOnlyList<CaretGroup> groups, Func<TextDocument.TextEdit, CaretGroup, IReadOnlyList<Selection>> apply)
    {
        var before = _selections;
        var primary = before.Primary;
        var after = Document.Edit(kind, before, edit =>
        {
            var results = new List<(Selection Selection, bool IsPrimary)>();
            void Shift(object? sender, TextChange change)
            {
                for (var i = 0; i < results.Count; i++)
                {
                    results[i] = (Map(results[i].Selection, change), results[i].IsPrimary);
                }
            }

            Document.Changed += Shift;
            _inCaretRun = true;
            try
            {
                for (var g = groups.Count - 1; g >= 0; g--)
                {
                    _caretIndex = g;
                    var produced = apply(edit, groups[g]);
                    for (var i = 0; i < produced.Count; i++)
                    {
                        results.Add((produced[i], groups[g].Selections[i] == primary));
                    }
                }
            }
            finally
            {
                Document.Changed -= Shift;
                _inCaretRun = false;
                _runEdit = null;
            }

            var primaryResult = results.FindIndex(r => r.IsPrimary) is var index and >= 0 ? results[index].Selection : results[^1].Selection;
            return SelectionSet.Create(results.Select(r => r.Selection), primaryResult).Clamp(Document.Length);
        });

        AdoptEditResult(after);
    }

    /// <summary>
    /// Takes the selections an edit (or undo/redo) produced. Unlike <see cref="SetSelections"/> it does
    /// not seal the undo step, so consecutive typing still merges into one step.
    /// </summary>
    private void AdoptEditResult(SelectionSet selections)
    {
        _selections = selections;
        _preferredColumns = null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Changes the document for the cursor being processed and sets its new selection.</summary>
    private void Edit(Func<TextDocument.TextEdit, Selection> edit)
    {
        if (_runEdit is not { } runEdit)
        {
            throw new InvalidOperationException("Edits must run through RunEdit or EditGroups.");
        }

        _current = edit(runEdit);
    }

    /// <summary>Where a selection ends up after a change elsewhere in the document.</summary>
    private static Selection Map(Selection selection, TextChange change) =>
        new(change.MapOffset(selection.Anchor), change.MapOffset(selection.Active));

    /// <summary>Cursors handled together by a line operation (they share lines).</summary>
    private sealed record CaretGroup(IReadOnlyList<Selection> Selections)
    {
        public int FirstLine { get; init; }

        public int LastLine { get; init; }
    }

    // Single-cursor movement helpers --------------------------------------------------------------

    private void MoveOne(CaretMovement movement, bool extend)
    {
        // Without Shift, Left/Right on a selection collapse it to the matching edge.
        if (!extend && !Selection.IsEmpty && movement is CaretMovement.Left or CaretMovement.Right)
        {
            SetSelection(Selection.Caret(movement == CaretMovement.Left ? Selection.Start : Selection.End));
            return;
        }

        var target = movement switch
        {
            CaretMovement.Left => PreviousCharOffset(Selection.Active),
            CaretMovement.Right => NextCharOffset(Selection.Active),
            CaretMovement.WordLeft => WordStartBefore(Selection.Active),
            CaretMovement.WordRight => WordEndAfter(Selection.Active),
            CaretMovement.LineStart => SmartLineStart(),
            CaretMovement.LineEnd => Document.GetLineEnd(CaretPosition.Line),
            CaretMovement.DocumentStart => 0,
            CaretMovement.DocumentEnd => Document.Length,
            _ => Selection.Active,
        };

        SetSelection(extend ? Selection.ExtendTo(target) : Selection.Caret(target));
    }

    private Selection MoveByLines(Selection caret, int lineDelta, bool extend, int preferredColumn)
    {
        var targetLine = Document.GetLineOfOffset(caret.Active) + lineDelta;
        int target;
        if (targetLine < 0)
        {
            target = 0;
        }
        else if (targetLine >= Document.LineCount)
        {
            target = Document.Length;
        }
        else
        {
            var column = TextColumns.FromVisual(Document.GetLineText(targetLine), preferredColumn, Options.TabSize);
            target = Document.GetLineStart(targetLine) + column;
        }

        return extend ? caret.ExtendTo(target) : Selection.Caret(target);
    }

    private int VisualColumnOf(int offset)
    {
        var position = Document.GetPosition(offset);
        return TextColumns.ToVisual(Document.GetLineText(position.Line), position.Column, Options.TabSize);
    }

    private int SmartLineStart()
    {
        var caret = CaretPosition;
        var lineStart = Document.GetLineStart(caret.Line);
        var indent = TextColumns.LeadingWhitespaceLength(Document.GetLineText(caret.Line));
        return caret.Column == indent ? lineStart : lineStart + indent;
    }

    private int PreviousCharOffset(int offset)
    {
        if (offset <= 0)
        {
            return 0;
        }

        // Step over a whole surrogate pair so the caret never splits an emoji.
        return offset >= 2 && char.IsLowSurrogate(Document[offset - 1]) && char.IsHighSurrogate(Document[offset - 2])
            ? offset - 2
            : offset - 1;
    }

    private int NextCharOffset(int offset)
    {
        if (offset >= Document.Length)
        {
            return Document.Length;
        }

        return offset + 1 < Document.Length && char.IsHighSurrogate(Document[offset]) && char.IsLowSurrogate(Document[offset + 1])
            ? offset + 2
            : offset + 1;
    }

    private int WordStartBefore(int offset)
    {
        var i = offset;
        while (i > 0 && Document[i - 1] is ' ' or '\t')
        {
            i--;
        }

        if (i > 0 && Document[i - 1] == '\n')
        {
            return i == offset ? i - 1 : i;
        }

        if (i == 0)
        {
            return 0;
        }

        var kind = CharClassifier.Classify(Document[i - 1]);
        while (i > 0 && Document[i - 1] != '\n' && CharClassifier.Classify(Document[i - 1]) == kind)
        {
            i--;
        }

        return i;
    }

    private int WordEndAfter(int offset)
    {
        var length = Document.Length;
        var i = offset;
        while (i < length && Document[i] is ' ' or '\t')
        {
            i++;
        }

        if (i < length && Document[i] == '\n')
        {
            return i == offset ? i + 1 : i;
        }

        if (i == length)
        {
            return length;
        }

        var kind = CharClassifier.Classify(Document[i]);
        while (i < length && Document[i] != '\n' && CharClassifier.Classify(Document[i]) == kind)
        {
            i++;
        }

        return i;
    }
}
