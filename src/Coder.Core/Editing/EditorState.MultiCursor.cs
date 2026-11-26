using Coder.Core.Text;

namespace Coder.Core.Editing;

/// <summary>Creating and removing extra cursors (VS Code key bindings in parentheses).</summary>
public sealed partial class EditorState
{
    /// <summary>
    /// Alt+Click: adds a cursor at <paramref name="offset"/>, or removes the cursor already there
    /// (the last cursor is never removed). The new cursor becomes primary.
    /// </summary>
    public void ToggleCaret(int offset)
    {
        var existing = _selections.All.FirstOrDefault(s => s.Start <= offset && offset <= s.End && s.IsEmpty);
        if (_selections.Count > 1 && existing == Selection.Caret(offset))
        {
            var remaining = _selections.All.Where(s => s != existing).ToList();
            SetSelections(SelectionSet.Create(remaining, remaining[^1]));
            return;
        }

        var added = Selection.Caret(offset);
        SetSelections(SelectionSet.Create([.. _selections.All, added], added));
    }

    /// <summary>
    /// Ctrl+Alt+Up / Ctrl+Alt+Down: adds a cursor on the line above the top cursor (or below the bottom
    /// one), in the primary cursor's column.
    /// </summary>
    public void AddCaretVertically(bool above)
    {
        var edge = above ? _selections.All[0] : _selections.All[^1];
        var line = Document.GetLineOfOffset(edge.Active) + (above ? -1 : 1);
        if (line < 0 || line >= Document.LineCount)
        {
            return;
        }

        var column = TextColumns.FromVisual(Document.GetLineText(line), VisualColumnOf(_selections.Primary.Active), Options.TabSize);
        var added = Selection.Caret(Document.GetLineStart(line) + column);
        SetSelections(SelectionSet.Create([.. _selections.All, added], added));
    }

    /// <summary>
    /// Ctrl+D: with nothing selected, selects the word at the cursor; otherwise adds a selection on the
    /// next occurrence of the selected text (wrapping around), which becomes primary.
    /// </summary>
    public void AddNextOccurrence()
    {
        var primary = _selections.Primary;
        if (primary.IsEmpty)
        {
            var (start, end) = WordRangeAt(primary.Active);
            if (end > start && CharClassifier.IsWordChar(Document[start]))
            {
                var word = new Selection(start, end);
                SetSelections(SelectionSet.Create(_selections.All.Select(s => s == primary ? word : s), word));
            }

            return;
        }

        var text = Document.GetText(primary.Start, primary.Length);
        var all = Document.GetText();
        var searchFrom = _selections.All[^1].End;
        var found = all.IndexOf(text, searchFrom, StringComparison.Ordinal);
        if (found < 0)
        {
            found = all.IndexOf(text, StringComparison.Ordinal); // wrap around
        }

        var next = new Selection(found, found + text.Length);
        if (found < 0 || _selections.All.Contains(next))
        {
            return;
        }

        SetSelections(SelectionSet.Create([.. _selections.All, next], next));
    }

    /// <summary>
    /// Ctrl+Shift+L: selects every occurrence of the selected text, or of the word at the cursor
    /// (whole words only in that case).
    /// </summary>
    public void SelectAllOccurrences()
    {
        var primary = _selections.Primary;
        var fromWord = primary.IsEmpty;
        var (start, end) = fromWord ? WordRangeAt(primary.Active) : (primary.Start, primary.End);
        if (end <= start || (fromWord && !CharClassifier.IsWordChar(Document[start])))
        {
            return;
        }

        var options = new Search.SearchOptions(MatchCase: true, WholeWord: fromWord);
        var matches = Search.TextSearch.FindAll(Document.GetText(), Document.GetText(start, end - start), options).Matches;
        var current = matches.Select((m, i) => (m, i)).FirstOrDefault(x => x.m.Start == start).i;
        SelectAll(matches, current);
    }

    /// <summary>Esc: goes back to the primary cursor only. Returns false when there was just one cursor.</summary>
    public bool CollapseToPrimary()
    {
        if (_selections.Count == 1)
        {
            return false;
        }

        SetSelection(_selections.Primary);
        return true;
    }
}
