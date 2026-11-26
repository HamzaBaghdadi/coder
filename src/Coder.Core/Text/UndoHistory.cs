namespace Coder.Core.Text;

/// <summary>Tells the history how an edit may merge with the previous one.</summary>
public enum EditKind
{
    /// <summary>Never merges (paste, line moves, commands).</summary>
    Command,

    /// <summary>Consecutive typed characters merge into one undo step.</summary>
    Typing,

    /// <summary>Consecutive backspace/delete presses merge into one undo step.</summary>
    Deleting,
}

/// <summary>One undoable step: a list of changes plus the selection before and after them.</summary>
internal sealed class UndoGroup(long id, EditKind kind, SelectionSet selectionBefore)
{
    public long Id { get; } = id;

    public EditKind Kind { get; } = kind;

    public List<TextChange> Changes { get; } = [];

    public SelectionSet SelectionBefore { get; } = selectionBefore;

    public SelectionSet SelectionAfter { get; set; } = selectionBefore;

    /// <summary>A sealed group never absorbs later edits (set when the caret moves away).</summary>
    public bool IsSealed { get; set; }
}

/// <summary>Bounded undo/redo stacks. Each state of the document is identified by a group id.</summary>
internal sealed class UndoHistory
{
    private const int MaxGroups = 5_000;

    private readonly List<UndoGroup> _undo = [];
    private readonly Stack<UndoGroup> _redo = new();
    private long _nextId = 1;

    /// <summary>Id of the state the document is in now; 0 is the state it was loaded in.</summary>
    public long CurrentStateId => _undo.Count > 0 ? _undo[^1].Id : 0;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Returns the group the next edit should be recorded in, merging when allowed.</summary>
    public UndoGroup GroupFor(EditKind kind, SelectionSet selectionBefore)
    {
        _redo.Clear();
        if (_undo.Count > 0)
        {
            var last = _undo[^1];
            var canMerge = kind != EditKind.Command && last.Kind == kind && !last.IsSealed
                           && last.SelectionAfter == selectionBefore;
            if (canMerge)
            {
                return last;
            }
        }

        var group = new UndoGroup(_nextId++, kind, selectionBefore);
        _undo.Add(group);
        if (_undo.Count > MaxGroups)
        {
            _undo.RemoveRange(0, MaxGroups / 10);
        }

        return group;
    }

    /// <summary>Drops a group that ended up recording nothing, so no-op edits never dirty the document.</summary>
    public void DiscardIfEmpty(UndoGroup group)
    {
        if (group.Changes.Count == 0 && _undo.Count > 0 && ReferenceEquals(_undo[^1], group))
        {
            _undo.RemoveAt(_undo.Count - 1);
        }
    }

    public void SealLast()
    {
        if (_undo.Count > 0)
        {
            _undo[^1].IsSealed = true;
        }
    }

    public UndoGroup? PopUndo()
    {
        if (_undo.Count == 0)
        {
            return null;
        }

        var group = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        group.IsSealed = true;
        _redo.Push(group);
        return group;
    }

    public UndoGroup? PopRedo()
    {
        if (!_redo.TryPop(out var group))
        {
            return null;
        }

        _undo.Add(group);
        return group;
    }
}
