using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Coder.Core.Editing;
using Coder.Core.Text;

namespace Coder.App.Editor;

/// <summary>Keyboard, text and pointer input. All editing decisions are delegated to <see cref="EditorState"/>.</summary>
public sealed partial class EditorView
{
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

    // Mouse selection in progress: what one drag step selects, and the range the drag started from.
    private DragUnit? _dragUnit;
    private (int Start, int End) _dragOrigin;
    private Point _lastDragPoint;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            UpdateCtrlLink(_pointer, e.KeyModifiers | KeyModifiers.Control); // Ctrl pressed over a symbol
            return;
        }

        EndHover();
        if (_editor is null || e.Handled)
        {
            return;
        }

        if (TryHandleClipboard(e) || TryHandlePaging(e))
        {
            e.Handled = true;
            return;
        }

        // Esc first drops extra cursors, then clears the selection; otherwise it is left for others (find widget).
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None
            && (TryEndSnippet(_editor) || _editor.CollapseToPrimary() || TryClearSelection(_editor)))
        {
            e.Handled = true;
            return;
        }

        if (EditorKeyMap.TryGet(e.Key, e.KeyModifiers, out var action))
        {
            action(_editor);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_editor is null || e.Handled || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        // Control characters (Enter, Tab, Backspace…) arrive here too; KeyDown already handled them.
        if (e.Text.Any(char.IsControl))
        {
            return;
        }

        _editor.TypeText(e.Text);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_editor is null || MinimapPressed(e) || StickyPressed(e))
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        var offset = OffsetAt(point.Position);
        var inGutter = point.Position.X < GutterWidth;
        var extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (inGutter && e.ClickCount == 1 && TryClickChangeMarker(point.Position))
        {
            e.Handled = true;
            return;
        }

        if (!inGutter && e.KeyModifiers == KeyModifiers.None && TryClickColorSwatch(point.Position))
        {
            e.Handled = true;
            return;
        }

        if (!inGutter && TryGoToDefinition(e, offset))
        {
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Alt && e.ClickCount == 1 && !inGutter)
        {
            _editor.ToggleCaret(offset); // Alt+Click adds (or removes) a cursor
            e.Handled = true;
            return;
        }

        // Click selects a caret position, double-click a word, triple-click (or the gutter) a line;
        // dragging afterwards extends the selection in the same unit, like VS Code.
        var unit = inGutter || e.ClickCount >= 3 ? DragUnit.Line : e.ClickCount == 2 ? DragUnit.Word : DragUnit.Character;
        if (unit == DragUnit.Character && extend)
        {
            _editor.SetCaret(offset, extend: true);
            _dragOrigin = (_editor.Selection.Anchor, _editor.Selection.Anchor);
        }
        else
        {
            _dragOrigin = RangeAt(offset, unit);
            _editor.SetSelection(new Selection(_dragOrigin.Start, _dragOrigin.End));
        }

        _dragUnit = unit;
        _lastDragPoint = point.Position;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl && !UpdateCtrlLink(_pointer, e.KeyModifiers & ~KeyModifiers.Control) && _pointer is { } point)
        {
            Cursor = point.X < GutterWidth ? Cursor.Default : TextCursor;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (MinimapMoved(e))
        {
            return;
        }

        var position = e.GetPosition(this);
        if (StickyMoved(position))
        {
            _pointer = null;
            UpdateCtrlLink(null, e.KeyModifiers);
            return;
        }

        Cursor = position.X < GutterWidth ? Cursor.Default : SwatchAt(position) is not null ? SwatchCursor : TextCursor;
        _pointer = position;
        UpdateCtrlLink(position, e.KeyModifiers);
        TrackHover(position);

        if (_dragUnit is not null)
        {
            _lastDragPoint = position;
            ExtendDragSelection();

            // Outside the text area the view keeps scrolling on a timer while the button is held.
            var outside = position.Y < 0 || position.Y > Bounds.Height || position.X < GutterWidth || position.X > Bounds.Width;
            if (outside)
            {
                _dragScrollTimer.Start();
            }
            else
            {
                _dragScrollTimer.Stop();
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (MinimapReleased(e))
        {
            return;
        }

        if (_dragUnit is not null)
        {
            EndDrag();
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        _dragUnit = null;
        _dragScrollTimer.Stop();
    }

    /// <summary>Selects from the drag origin to the unit (character, word or line) under the pointer.</summary>
    private void ExtendDragSelection()
    {
        if (_editor is null || _dragUnit is not { } unit)
        {
            return;
        }

        var (start, end) = RangeAt(OffsetAt(_lastDragPoint), unit);
        _editor.SetSelection(start < _dragOrigin.Start
            ? new Selection(_dragOrigin.End, start)                          // dragging backwards
            : new Selection(_dragOrigin.Start, Math.Max(end, _dragOrigin.End)));
    }

    /// <summary>While dragging beyond an edge: scroll toward the pointer, faster the further out it is.</summary>
    private void DragScrollTick()
    {
        if (_dragUnit is null)
        {
            _dragScrollTimer.Stop();
            return;
        }

        static double Speed(double distance) => Math.Clamp(distance, 2, 60);
        var p = _lastDragPoint;
        var dy = p.Y < 0 ? -Speed(-p.Y) : p.Y > Bounds.Height ? Speed(p.Y - Bounds.Height) : 0;
        var dx = p.X < GutterWidth ? -Speed(GutterWidth - p.X) : p.X > Bounds.Width ? Speed(p.X - Bounds.Width) : 0;
        Offset = _offset + new Vector(dx, dy);
        RaiseScrollInvalidated(EventArgs.Empty);
        ExtendDragSelection();
    }

    private (int Start, int End) RangeAt(int offset, DragUnit unit)
    {
        var document = _editor!.Document;
        switch (unit)
        {
            case DragUnit.Word:
                return _editor.WordRangeAt(offset);
            case DragUnit.Line:
                var line = document.GetLineOfOffset(offset);
                return (document.GetLineStart(line), line + 1 < document.LineCount ? document.GetLineStart(line + 1) : document.Length);
            default:
                return (offset, offset);
        }
    }

    private enum DragUnit
    {
        Character,
        Word,
        Line,
    }

    /// <summary>Esc while filling in a snippet only leaves the snippet (the selection stays).</summary>
    private static bool TryEndSnippet(EditorState editor)
    {
        if (!editor.IsInSnippet)
        {
            return false;
        }

        editor.EndSnippet();
        return true;
    }

    private static bool TryClearSelection(EditorState editor)
    {
        if (editor.Selection.IsEmpty)
        {
            return false;
        }

        editor.SetCaret(editor.Selection.Active);
        return true;
    }

    /// <summary>Maps a point in the control to the nearest document offset, using the line's real layout.</summary>
    private int OffsetAt(Point point)
    {
        var document = _editor!.Document;
        var line = Math.Clamp((int)((point.Y + _offset.Y) / _metrics.LineHeight), 0, document.LineCount - 1);
        return document.GetLineStart(line) + ColumnAtX(line, point.X);
    }

    private bool TryHandlePaging(KeyEventArgs e)
    {
        if (e.Key is not (Key.PageUp or Key.PageDown) || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift))
        {
            return false;
        }

        var pageLines = Math.Max(1, (int)(_viewport.Height / _metrics.LineHeight) - 1);
        var direction = e.Key == Key.PageUp ? -1 : 1;
        Offset = _offset.WithY(_offset.Y + direction * pageLines * _metrics.LineHeight);
        RaiseScrollInvalidated(EventArgs.Empty);
        _editor!.MoveCaretByLines(direction * pageLines, e.KeyModifiers == KeyModifiers.Shift);
        return true;
    }

    private bool TryHandleClipboard(KeyEventArgs e)
    {
        var isCtrl = e.KeyModifiers == KeyModifiers.Control;
        var isShift = e.KeyModifiers == KeyModifiers.Shift;
        var operation = (e.Key, isCtrl, isShift) switch
        {
            (Key.C, true, _) or (Key.Insert, true, _) => ClipboardOperation.Copy,
            (Key.X, true, _) or (Key.Delete, _, true) => ClipboardOperation.Cut,
            (Key.V, true, _) or (Key.Insert, _, true) => ClipboardOperation.Paste,
            _ => (ClipboardOperation?)null,
        };

        if (operation is not { } op || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return false;
        }

        _ = RunClipboardAsync(op, clipboard, _editor!);
        return true;
    }

    private static async Task RunClipboardAsync(ClipboardOperation operation, IClipboard clipboard, EditorState editor)
    {
        switch (operation)
        {
            case ClipboardOperation.Copy:
                await clipboard.SetTextAsync(editor.GetCopyText());
                break;
            case ClipboardOperation.Cut:
                await clipboard.SetTextAsync(editor.GetCopyText());
                editor.Cut();
                break;
            case ClipboardOperation.Paste:
                if (await clipboard.TryGetTextAsync() is { Length: > 0 } text)
                {
                    editor.Paste(text);
                }

                break;
        }
    }

    private enum ClipboardOperation
    {
        Copy,
        Cut,
        Paste,
    }
}
