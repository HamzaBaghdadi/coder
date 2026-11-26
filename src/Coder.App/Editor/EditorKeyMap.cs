using Avalonia.Input;
using Coder.Core.Editing;

namespace Coder.App.Editor;

/// <summary>
/// Declarative key bindings for the text area. Each entry maps a key + modifiers to one operation on
/// <see cref="EditorState"/>; the view never contains editing logic itself.
/// Clipboard keys are handled by the view because they are asynchronous.
/// </summary>
internal static class EditorKeyMap
{
    private const KeyModifiers None = KeyModifiers.None;
    private const KeyModifiers Shift = KeyModifiers.Shift;
    private const KeyModifiers Ctrl = KeyModifiers.Control;
    private const KeyModifiers Alt = KeyModifiers.Alt;

    private static readonly Dictionary<(Key, KeyModifiers), Action<EditorState>> Bindings = Build();

    public static bool TryGet(Key key, KeyModifiers modifiers, out Action<EditorState> action) =>
        Bindings.TryGetValue((key, modifiers), out action!);

    private static Dictionary<(Key, KeyModifiers), Action<EditorState>> Build()
    {
        var map = new Dictionary<(Key, KeyModifiers), Action<EditorState>>();

        // Caret movement; the Shift variant of each extends the selection.
        AddMove(map, Key.Left, None, CaretMovement.Left);
        AddMove(map, Key.Right, None, CaretMovement.Right);
        AddMove(map, Key.Up, None, CaretMovement.Up);
        AddMove(map, Key.Down, None, CaretMovement.Down);
        AddMove(map, Key.Left, Ctrl, CaretMovement.WordLeft);
        AddMove(map, Key.Right, Ctrl, CaretMovement.WordRight);
        AddMove(map, Key.Home, None, CaretMovement.LineStart);
        AddMove(map, Key.End, None, CaretMovement.LineEnd);
        AddMove(map, Key.Home, Ctrl, CaretMovement.DocumentStart);
        AddMove(map, Key.End, Ctrl, CaretMovement.DocumentEnd);

        // Editing.
        map[(Key.Enter, None)] = e => e.InsertNewLine();
        map[(Key.Enter, Shift)] = e => e.InsertNewLine();
        map[(Key.Enter, Ctrl)] = e => e.InsertLineAndMove(above: false);
        map[(Key.Enter, Ctrl | Shift)] = e => e.InsertLineAndMove(above: true);
        map[(Key.Back, None)] = e => e.Backspace();
        map[(Key.Back, Shift)] = e => e.Backspace();
        map[(Key.Back, Ctrl)] = e => e.DeleteWordLeft();
        map[(Key.Delete, None)] = e => e.DeleteForward();
        map[(Key.Delete, Ctrl)] = e => e.DeleteWordRight();
        map[(Key.Tab, None)] = e => { if (!e.MoveToTabStop(backwards: false)) { e.Indent(); } };
        map[(Key.Tab, Shift)] = e => { if (!e.MoveToTabStop(backwards: true)) { e.Outdent(); } };
        map[(Key.OemCloseBrackets, Ctrl)] = e => e.Indent();
        map[(Key.OemOpenBrackets, Ctrl)] = e => e.Outdent();

        // Selection and history.
        map[(Key.A, Ctrl)] = e => e.SelectAll();
        map[(Key.Z, Ctrl)] = e => e.Undo();
        map[(Key.Y, Ctrl)] = e => e.Redo();
        map[(Key.Z, Ctrl | Shift)] = e => e.Redo();

        // Line operations (VS Code defaults).
        map[(Key.OemQuestion, Ctrl)] = e => e.ToggleLineComment();
        map[(Key.Up, Alt)] = e => e.MoveLines(up: true);
        map[(Key.Down, Alt)] = e => e.MoveLines(up: false);
        map[(Key.Up, Alt | Shift)] = e => e.DuplicateLines(down: false);
        map[(Key.Down, Alt | Shift)] = e => e.DuplicateLines(down: true);
        map[(Key.K, Ctrl | Shift)] = e => e.DeleteLines();
        map[(Key.L, Ctrl)] = e => e.SelectLine(e.CaretPosition.Line);

        // Multiple cursors (VS Code defaults). Alt+Click is handled by the view.
        map[(Key.D, Ctrl)] = e => e.AddNextOccurrence();
        map[(Key.L, Ctrl | Shift)] = e => e.SelectAllOccurrences();
        map[(Key.Up, Ctrl | Alt)] = e => e.AddCaretVertically(above: true);
        map[(Key.Down, Ctrl | Alt)] = e => e.AddCaretVertically(above: false);
        return map;
    }

    private static void AddMove(Dictionary<(Key, KeyModifiers), Action<EditorState>> map, Key key, KeyModifiers modifiers, CaretMovement movement)
    {
        map[(key, modifiers)] = e => e.MoveCaret(movement);
        map[(key, modifiers | Shift)] = e => e.MoveCaret(movement, extend: true);
    }
}
