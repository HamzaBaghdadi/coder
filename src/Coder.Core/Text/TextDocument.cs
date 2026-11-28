namespace Coder.Core.Text;

/// <summary>
/// An editable text document: storage, change notifications and undo history.
/// All modifications go through <see cref="Edit"/> so that every change is undoable and observed.
/// </summary>
public sealed class TextDocument
{
    private readonly PieceTable _text;
    private readonly UndoHistory _history = new();
    private long _savedStateId;

    public TextDocument(string text = "")
    {
        LineEnding = LineEndings.Detect(text);
        _text = new PieceTable(LineEndings.Normalize(text));
    }

    /// <summary>Raised after every individual change, including those made by undo and redo.</summary>
    public event EventHandler<TextChange>? Changed;

    /// <summary>Raised when <see cref="IsModified"/> may have flipped.</summary>
    public event EventHandler? ModifiedChanged;

    public LineEnding LineEnding { get; set; }

    /// <summary>Increments on every change. Cheap way for caches to know they are stale.</summary>
    public int Version { get; private set; }

    public int Length => _text.Length;

    public int LineCount => _text.LineCount;

    public bool IsModified => _history.CurrentStateId != _savedStateId;

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    public char this[int offset] => _text[offset];

    public string GetText() => _text.GetText();

    public string GetText(int offset, int length) => _text.GetText(offset, length);

    public string GetLineText(int line) => _text.GetLineText(line);

    public int GetLineStart(int line) => _text.GetLineStart(line);

    public int GetLineLength(int line) => _text.GetLineLength(line);

    public int GetLineEnd(int line) => GetLineStart(line) + GetLineLength(line);

    public int GetLineOfOffset(int offset) => _text.GetLineOfOffset(offset);

    public TextPosition GetPosition(int offset)
    {
        var line = _text.GetLineOfOffset(offset);
        return new TextPosition(line, offset - _text.GetLineStart(line));
    }

    public int GetOffset(TextPosition position)
    {
        var line = Math.Clamp(position.Line, 0, LineCount - 1);
        return GetLineStart(line) + Math.Clamp(position.Column, 0, GetLineLength(line));
    }

    /// <summary>The document text using its original line endings, ready to be written to disk.</summary>
    public string GetTextForSave() => LineEndings.Apply(GetText(), LineEnding);

    public void MarkSaved()
    {
        _savedStateId = _history.CurrentStateId;
        ModifiedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Runs <paramref name="edit"/> as one undoable step. The callback performs changes through the
    /// supplied <see cref="TextEdit"/> and returns the selections the editor should show afterwards.
    /// </summary>
    public SelectionSet Edit(EditKind kind, SelectionSet selectionBefore, Func<TextEdit, SelectionSet> edit)
    {
        if (IsReadOnly)
        {
            return selectionBefore; // shown only (a response); whoever owns it lifts this to change it
        }

        var wasModified = IsModified;
        var group = _history.GroupFor(kind, selectionBefore);
        var selectionAfter = edit(new TextEdit(this, group));
        group.SelectionAfter = selectionAfter;
        _history.DiscardIfEmpty(group);
        NotifyIfModifiedChanged(wasModified);
        return selectionAfter;
    }

    /// <summary>Refuses edits (typing, pasting, undo): the text is only shown, as in a response viewer.</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>Stops the latest undo step from absorbing further typing.</summary>
    public void SealUndoStep() => _history.SealLast();

    /// <summary>Undoes one step and returns the selections to restore, or null if there was nothing to undo.</summary>
    public SelectionSet? Undo()
    {
        if (IsReadOnly)
        {
            return null;
        }

        var wasModified = IsModified;
        var group = _history.PopUndo();
        if (group is null)
        {
            return null;
        }

        for (var i = group.Changes.Count - 1; i >= 0; i--)
        {
            Apply(group.Changes[i].Invert());
        }

        NotifyIfModifiedChanged(wasModified);
        return group.SelectionBefore;
    }

    public SelectionSet? Redo()
    {
        if (IsReadOnly)
        {
            return null;
        }

        var wasModified = IsModified;
        var group = _history.PopRedo();
        if (group is null)
        {
            return null;
        }

        foreach (var change in group.Changes)
        {
            Apply(change);
        }

        NotifyIfModifiedChanged(wasModified);
        return group.SelectionAfter;
    }

    private TextChange Replace(int offset, int length, string text)
    {
        var change = new TextChange(offset, _text.GetText(offset, length), text, _text.GetLineOfOffset(offset));
        Apply(change);
        return change;
    }

    private void Apply(TextChange change)
    {
        _text.Delete(change.Offset, change.RemovedText.Length);
        _text.Insert(change.Offset, change.InsertedText);
        Version++;
        Changed?.Invoke(this, change);
    }

    private void NotifyIfModifiedChanged(bool wasModified)
    {
        if (wasModified != IsModified)
        {
            ModifiedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The write handle passed to <see cref="Edit"/>. Read access stays on the document.</summary>
    public readonly struct TextEdit
    {
        private readonly TextDocument _document;
        private readonly UndoGroup _group;

        internal TextEdit(TextDocument document, UndoGroup group)
        {
            _document = document;
            _group = group;
        }

        public TextDocument Document => _document;

        public void Replace(int offset, int length, string text)
        {
            if (length == 0 && text.Length == 0)
            {
                return;
            }

            _group.Changes.Add(_document.Replace(offset, length, text));
        }

        public void Insert(int offset, string text) => Replace(offset, 0, text);

        public void Delete(int offset, int length) => Replace(offset, length, string.Empty);
    }
}
