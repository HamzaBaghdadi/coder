using Coder.Core.Text;

namespace Coder.Core.Editing;

/// <summary>
/// Typing, deleting and clipboard operations. Each public command runs its single-cursor logic once
/// per cursor (see <see cref="RunEdit"/>); the private <c>…AtCaret</c> methods read <see cref="Selection"/>.
/// </summary>
public sealed partial class EditorState
{
    /// <summary>Inserts typed text at every cursor, applying auto-closing pairs for brackets and quotes.</summary>
    public void TypeText(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        RunEdit(EditKind.Typing, () => TypeTextAtCaret(text));
        if (char.IsWhiteSpace(text[^1]))
        {
            // Words become separate undo steps, like in most editors.
            Document.SealUndoStep();
        }
    }

    /// <summary>Enter: keeps the current indentation and indents further after an opening bracket.</summary>
    public void InsertNewLine() => RunEdit(EditKind.Command, InsertNewLineAtCaret);

    /// <summary>
    /// Pastes at every cursor. When the clipboard has exactly one line per cursor (as copied from
    /// several cursors), each cursor gets its own line.
    /// </summary>
    public void Paste(string text)
    {
        var normalized = LineEndings.Normalize(text);
        var lines = normalized.Split('\n');
        var distribute = _selections.Count > 1 && lines.Length == _selections.Count;
        RunEdit(EditKind.Command, () => ReplaceSelection(distribute ? lines[_caretIndex] : normalized));
    }

    /// <summary>
    /// Text for Copy/Cut: the selected text of every cursor, one per line. A cursor without a selection
    /// contributes its whole line, like VS Code.
    /// </summary>
    public string GetCopyText()
    {
        if (_selections.Count == 1)
        {
            var selection = _selections.Primary;
            return selection.IsEmpty
                ? Document.GetLineText(Document.GetLineOfOffset(selection.Active)) + "\n"
                : Document.GetText(selection.Start, selection.Length);
        }

        return string.Join("\n", _selections.All.Select(selection => selection.IsEmpty
            ? Document.GetLineText(Document.GetLineOfOffset(selection.Active))
            : Document.GetText(selection.Start, selection.Length)));
    }

    public void Cut()
    {
        if (_selections.All.All(selection => selection.IsEmpty))
        {
            DeleteLines();
            return;
        }

        RunEdit(EditKind.Command, () => ReplaceSelection(string.Empty));
    }

    public void Backspace() => RunEdit(EditKind.Deleting, BackspaceAtCaret);

    public void DeleteForward() => RunEdit(EditKind.Deleting, () =>
    {
        if (!Selection.IsEmpty)
        {
            ReplaceSelection(string.Empty);
            return;
        }

        DeleteRange(Selection.Active, NextCharOffset(Selection.Active));
    });

    public void DeleteWordLeft() => RunEdit(EditKind.Deleting, () =>
    {
        if (!Selection.IsEmpty)
        {
            ReplaceSelection(string.Empty);
            return;
        }

        DeleteRange(WordStartBefore(Selection.Active), Selection.Active);
    });

    public void DeleteWordRight() => RunEdit(EditKind.Deleting, () =>
    {
        if (!Selection.IsEmpty)
        {
            ReplaceSelection(string.Empty);
            return;
        }

        DeleteRange(Selection.Active, WordEndAfter(Selection.Active));
    });

    public void Undo()
    {
        EndSnippet();
        Restore(Document.Undo());
    }

    public void Redo()
    {
        EndSnippet();
        Restore(Document.Redo());
    }

    private void Restore(SelectionSet? selections)
    {
        if (selections is null)
        {
            return;
        }

        AdoptEditResult(selections.Clamp(Document.Length));
    }

    private void TypeTextAtCaret(string text)
    {
        if (text.Length == 1 && Options.AutoClosePairs && TryTypePairCharacter(text[0]))
        {
            return;
        }

        ReplaceSelection(text);
    }

    private void InsertNewLineAtCaret()
    {
        var caret = CaretPosition;
        var lineText = Document.GetLineText(caret.Line);
        var indent = lineText[..Math.Min(TextColumns.LeadingWhitespaceLength(lineText), caret.Column)];
        var before = lineText[..caret.Column].TrimEnd();
        var after = Selection.IsEmpty ? lineText[caret.Column..].TrimStart() : string.Empty;

        var closer = '\0';
        var opensBlock = before.Length > 0 && TryGetClosingBracket(before[^1], out closer);
        if (!opensBlock)
        {
            ReplaceSelection("\n" + indent);
            return;
        }

        var inner = "\n" + indent + Options.IndentUnit;
        var closesBlock = after.Length > 0 && after[0] == closer;
        Edit(edit =>
        {
            var insertion = closesBlock ? inner + "\n" + indent : inner;
            edit.Replace(Selection.Start, Selection.Length, insertion);
            return Selection.Caret(Selection.Start + inner.Length);
        });
    }

    private void BackspaceAtCaret()
    {
        if (!Selection.IsEmpty)
        {
            ReplaceSelection(string.Empty);
            return;
        }

        var caret = Selection.Active;
        if (caret == 0)
        {
            return;
        }

        // Deleting the opening half of an empty auto-closed pair removes both halves: "(|)" -> "|".
        if (caret < Document.Length && IsAutoClosePair(Document[caret - 1], Document[caret]))
        {
            DeleteRange(caret - 1, caret + 1);
            return;
        }

        DeleteRange(BackspaceTarget(caret), caret);
    }

    private void ReplaceSelection(string text) =>
        Edit(edit =>
        {
            var start = Selection.Start;
            edit.Replace(start, Selection.Length, text);
            return Selection.Caret(start + text.Length);
        });

    private void DeleteRange(int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        Edit(edit =>
        {
            edit.Delete(start, end - start);
            return Selection.Caret(start);
        });
    }

    /// <summary>Inside leading spaces Backspace removes a whole indent level, otherwise one character.</summary>
    private int BackspaceTarget(int caret)
    {
        var position = Document.GetPosition(caret);
        var lineText = Document.GetLineText(position.Line);
        var inIndent = position.Column > 0 && position.Column <= TextColumns.LeadingWhitespaceLength(lineText);
        if (!Options.InsertSpaces || !inIndent || lineText[position.Column - 1] != ' ')
        {
            return PreviousCharOffset(caret);
        }

        var visual = TextColumns.ToVisual(lineText, position.Column, Options.TabSize);
        var previousStop = (visual - 1) / Options.TabSize * Options.TabSize;
        var column = position.Column;
        while (column > 0 && lineText[column - 1] == ' ' && TextColumns.ToVisual(lineText, column - 1, Options.TabSize) >= previousStop)
        {
            column--;
        }

        return caret - (position.Column - column);
    }

    private bool TryTypePairCharacter(char c)
    {
        var caret = Selection.Active;
        var next = caret < Document.Length ? Document[caret] : '\0';

        // Typing a closer right before the same closer just steps over it.
        if (Selection.IsEmpty && next == c && IsCloser(c))
        {
            SetCaret(caret + 1);
            return true;
        }

        if (!TryGetClosingBracket(c, out var closer) && !IsQuote(c, out closer))
        {
            return false;
        }

        // Wrap a selection: "abc" -> "(abc)", keeping the text selected.
        if (!Selection.IsEmpty)
        {
            var selection = Selection;
            Edit(edit =>
            {
                edit.Insert(selection.End, closer.ToString());
                edit.Insert(selection.Start, c.ToString());
                return new Selection(selection.Anchor + 1, selection.Active + 1);
            });
            return true;
        }

        var nextAllowsPair = next == '\0' || char.IsWhiteSpace(next) || IsCloser(next) || next is ',' or ';';
        var previous = caret > 0 ? Document[caret - 1] : '\0';
        var quoteInsideWord = IsQuote(c, out _) && CharClassifier.IsWordChar(previous);
        if (!nextAllowsPair || quoteInsideWord)
        {
            return false;
        }

        Edit(edit =>
        {
            edit.Insert(caret, $"{c}{closer}");
            return Selection.Caret(caret + 1);
        });
        return true;
    }

    private bool TryGetClosingBracket(char open, out char close)
    {
        var brackets = Language.Brackets;
        for (var i = 0; i + 1 < brackets.Length; i += 2)
        {
            if (brackets[i] == open)
            {
                close = brackets[i + 1];
                return true;
            }
        }

        close = '\0';
        return false;
    }

    private bool IsQuote(char c, out char close)
    {
        close = c;
        return Language.StringQuotes.Contains(c);
    }

    private bool IsCloser(char c)
    {
        var brackets = Language.Brackets;
        for (var i = 1; i < brackets.Length; i += 2)
        {
            if (brackets[i] == c)
            {
                return true;
            }
        }

        return Language.StringQuotes.Contains(c);
    }

    private bool IsAutoClosePair(char open, char close) =>
        Options.AutoClosePairs &&
        ((TryGetClosingBracket(open, out var expected) && expected == close) || (IsQuote(open, out _) && open == close));
}
