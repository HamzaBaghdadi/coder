using Avalonia;
using Avalonia.Input.TextInput;

namespace Coder.App.Editor;

/// <summary>
/// Connects the editor to the operating system's input methods (IME) for Chinese, Japanese, Korean and
/// other composed input:
/// <list type="bullet">
/// <item>reports the caret rectangle, so the candidate window opens right at the caret;</item>
/// <item>shows the text being composed (preedit) at the caret until it is committed;</item>
/// <item>exposes the current line, so input methods that read context can do so.</item>
/// </list>
/// Committed text arrives as a normal text-input event and is typed at every cursor.
/// </summary>
internal sealed class EditorInputMethod(EditorView view) : TextInputMethodClient
{
    public override Visual TextViewVisual => view;

    public override bool SupportsPreedit => true;

    public override bool SupportsSurroundingText => true;

    /// <summary>The primary caret's line.</summary>
    public override string SurroundingText =>
        view.Editor is { } editor ? editor.Document.GetLineText(editor.CaretPosition.Line) : string.Empty;

    public override Rect CursorRectangle => view.CaretRectangle;

    /// <summary>The primary selection, as columns within <see cref="SurroundingText"/>.</summary>
    public override TextSelection Selection
    {
        get
        {
            if (view.Editor is not { } editor)
            {
                return default;
            }

            var lineStart = editor.Document.GetLineStart(editor.CaretPosition.Line);
            var lineEnd = editor.Document.GetLineEnd(editor.CaretPosition.Line);
            var selection = editor.Selection;
            return new TextSelection(
                Math.Clamp(selection.Anchor, lineStart, lineEnd) - lineStart,
                Math.Clamp(selection.Active, lineStart, lineEnd) - lineStart);
        }

        set
        {
            if (view.Editor is not { } editor)
            {
                return;
            }

            var lineStart = editor.Document.GetLineStart(editor.CaretPosition.Line);
            editor.SetSelection(new Core.Text.Selection(lineStart + value.Start, lineStart + value.End));
        }
    }

    public override void SetPreeditText(string? preeditText) => view.SetPreedit(preeditText);

    /// <summary>Called when the caret or text changes, so the input method follows along.</summary>
    public void NotifyCaretChanged()
    {
        RaiseCursorRectangleChanged();
        RaiseSelectionChanged();
        RaiseSurroundingTextChanged();
    }
}
