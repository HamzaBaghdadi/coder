using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Coder.App.Editor;
using Coder.App.Theme;
using Coder.Core.Terminal;

namespace Coder.App.Terminal;

/// <summary>
/// Shows a <see cref="TerminalSession"/> and sends it the user's keys. Draws only the visible rows of
/// the cell grid; the mouse wheel scrolls back through history. Selection follows terminal
/// conventions: drag to select, double-click for a word, Ctrl+C copies when something is selected
/// (otherwise it interrupts the program), right-click copies a selection or pastes.
/// <para>Drawing lives in <c>TerminalView.Rendering.cs</c>.</para>
/// </summary>
public sealed partial class TerminalView : Control
{
    /// <summary>Around the cells; the left one also holds the command marks (shell integration).</summary>
    private const double Padding = 14;
    private const int WheelLines = 3;

    private readonly DispatcherTimer _cursorBlink;
    private EditorMetrics _metrics;
    private TerminalSession? _session;
    private int _scrollOffset; // lines scrolled up from the bottom
    private bool _cursorOn = true;
    private (int Line, int Column)? _selectionAnchor;
    private (int Line, int Column) _selectionActive;
    private bool _selecting;
    private IReadOnlyList<TerminalMatch> _matches = [];
    private int _currentMatch = -1;

    static TerminalView()
    {
        FocusableProperty.OverrideDefaultValue<TerminalView>(true);
        CursorProperty.OverrideDefaultValue<TerminalView>(new Cursor(StandardCursorType.Ibeam));
    }

    /// <summary>The terminal font (settings); the code font list follows it for missing characters.</summary>
    private FontFamily _family = Typography.Code;

    public TerminalView()
    {
        _metrics = new EditorMetrics(new Typeface(Typography.Code), 13, 1.35);
        _cursorBlink = new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Render, (_, _) =>
        {
            _cursorOn = !_cursorOn;
            InvalidateVisual();
        });
    }

    /// <summary>Enter was pressed after the shell exited: the owner should start a new one.</summary>
    public event EventHandler? RestartRequested;

    /// <summary>The grid size that fits the current bounds.</summary>
    public (int Columns, int Rows) GridSize => (
        Math.Max(10, (int)((Bounds.Width - Padding * 2) / _metrics.CharWidth)),
        Math.Max(3, (int)((Bounds.Height - Padding) / _metrics.LineHeight)));

    public TerminalSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null)
            {
                _session.ScreenChanged -= OnScreenChanged;
            }

            _session = value;
            _scrollOffset = 0;
            _selectionAnchor = null;
            _rowCache.Clear();
            if (_session is not null)
            {
                _session.ScreenChanged += OnScreenChanged;
                ResizeSession();
            }

            InvalidateVisual();
            ScrollChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Ctrl+F was pressed in the terminal: the owner opens its find bar.</summary>
    public event EventHandler? FindRequested;

    /// <summary>Find matches to highlight; <paramref name="current"/> is the one shown more strongly (-1: none).</summary>
    public void SetMatches(IReadOnlyList<TerminalMatch> matches, int current)
    {
        (_matches, _currentMatch) = (matches, current);
        InvalidateVisual();
    }

    /// <summary>Scrolls so that history line <paramref name="line"/> is in the middle of the view.</summary>
    public void Reveal(int line)
    {
        if (Buffer is { } buffer)
        {
            var top = Math.Clamp(line - buffer.Rows / 2, 0, buffer.TotalLines - buffer.Rows);
            ScrollOffset = buffer.TotalLines - buffer.Rows - top;
        }
    }

    /// <summary>The scrollback, the view's position in it, or the screen size changed (the scroll bar follows).</summary>
    public event EventHandler? ScrollChanged;

    /// <summary>Lines of history above the screen; none while a full-screen program uses the alternate screen.</summary>
    public int ScrollbackLines => Buffer is { IsAlternateScreen: false } buffer ? buffer.ScrollbackCount : 0;

    /// <summary>Rows on screen.</summary>
    public int VisibleRows => Buffer?.Rows ?? 0;

    /// <summary>Lines scrolled up from the live screen (0 = the bottom, following new output).</summary>
    public int ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            var clamped = Math.Clamp(value, 0, ScrollbackLines);
            if (clamped != _scrollOffset)
            {
                _scrollOffset = clamped;
                InvalidateVisual();
                ScrollChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private TerminalBuffer? Buffer => _session?.Emulator.Buffer;

    private bool HasSelection => _selectionAnchor is { } anchor && anchor != _selectionActive;

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        Dispatcher.UIThread.Post(ResizeSession); // after Bounds is updated
        return size;
    }

    /// <summary>Uses <paramref name="family"/> (empty: the code font list) at <paramref name="size"/>, and refits the grid.</summary>
    public void SetFont(string family, double size)
    {
        _family = string.IsNullOrWhiteSpace(family) ? Typography.Code : new FontFamily($"{family}, {Typography.Code}");
        _metrics = new EditorMetrics(new Typeface(_family), Math.Clamp(size, 8, 40), 1.35);
        _rowCache.Clear();
        ResizeSession();
        InvalidateVisual();
    }

    private void ResizeSession()
    {
        if (_session is not null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            var (columns, rows) = GridSize;
            _session.Resize(columns, rows);
            InvalidateVisual();
            ScrollChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnScreenChanged(object? sender, EventArgs e)
    {
        // New output keeps the view pinned to the bottom unless the user scrolled up to read.
        if (_scrollOffset > 0)
        {
            _scrollOffset = Math.Min(_scrollOffset, ScrollbackLines);
        }

        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty); // the scrollback grew
    }

    // Keyboard ---------------------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_session is null || e.Handled)
        {
            return;
        }

        if (_session.HasExited)
        {
            if (e.Key == Key.Enter)
            {
                RestartRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }

            return;
        }

        if (TryHandleClipboard(e))
        {
            e.Handled = true;
            return;
        }

        if (TryScrollToCommand(e))
        {
            e.Handled = true;
            return;
        }

        // Ctrl+F finds in the terminal (as in VS Code) rather than reaching the shell.
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            FindRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        var sequence = TerminalKeys.Encode(e.Key, e.KeyModifiers, _session.Emulator.ApplicationCursorKeys);
        if (sequence is not null)
        {
            Send(sequence);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_session is null || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl))
        {
            return; // control keys were sent by OnKeyDown
        }

        Send(e.Text);
        e.Handled = true;
    }

    /// <summary>Typing snaps back to the live screen and clears any selection.</summary>
    private void Send(string text)
    {
        ScrollOffset = 0;
        _selectionAnchor = null;
        _session!.Write(text);
        RestartCursorBlink();
    }

    private bool TryHandleClipboard(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers == KeyModifiers.Control;
        var ctrlShift = e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift);
        if ((e.Key == Key.C && ctrl && HasSelection) || (e.Key == Key.C && ctrlShift))
        {
            _ = CopySelectionAsync();
            return true;
        }

        if ((e.Key == Key.V && (ctrl || ctrlShift)) || (e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Shift))
        {
            _ = PasteAsync();
            return true;
        }

        return false;
    }

    private async Task CopySelectionAsync()
    {
        if (SelectedText() is { Length: > 0 } text && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }

        _selectionAnchor = null;
        InvalidateVisual();
    }

    private async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { Length: > 0 } text)
        {
            ScrollOffset = 0;
            _session?.Paste(text);
        }
    }

    // Mouse ------------------------------------------------------------------------------------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Buffer is not { } buffer || buffer.IsAlternateScreen)
        {
            return; // full-screen programs own their own scrolling
        }

        ScrollOffset = _scrollOffset + (int)Math.Round(e.Delta.Y * WheelLines);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (Buffer is null)
        {
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            _ = HasSelection ? CopySelectionAsync() : PasteAsync();
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (TryActivateLink(point.Position, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        var cell = CellAt(point.Position);
        if (e.ClickCount == 2)
        {
            SelectWordAt(cell);
        }
        else
        {
            _selectionAnchor = cell;
            _selectionActive = cell;
            _selecting = true;
            e.Pointer.Capture(this);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_selecting)
        {
            _selectionActive = CellAt(e.GetPosition(this));
            InvalidateVisual();
        }
        else
        {
            UpdateHoverLink(e.GetPosition(this), e.KeyModifiers);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverLink is not null)
        {
            _hoverLink = null;
            ToolTip.SetTip(this, null);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        RestartCursorBlink();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _cursorBlink.Stop();
        _cursorOn = true;
        InvalidateVisual();
    }

    private void RestartCursorBlink()
    {
        _cursorOn = true;
        _cursorBlink.Stop();
        if (IsFocused)
        {
            _cursorBlink.Start();
        }

        InvalidateVisual();
    }

    /// <summary>Absolute line index of the top visible row.</summary>
    private int TopLine => Buffer is { } b ? b.TotalLines - b.Rows - _scrollOffset : 0;

    /// <summary>The cell under a point, as (absolute line, column).</summary>
    private (int Line, int Column) CellAt(Point point)
    {
        var buffer = Buffer!;
        var row = Math.Clamp((int)((point.Y - Padding / 2) / _metrics.LineHeight), 0, buffer.Rows - 1);
        var column = Math.Clamp((int)Math.Round((point.X - Padding) / _metrics.CharWidth), 0, buffer.Columns);
        return (TopLine + row, column);
    }

    private void SelectWordAt((int Line, int Column) cell)
    {
        var cells = Buffer!.LineAt(cell.Line).Cells;
        static bool IsWordCell(TerminalCell c) => c.Rune != 0 && !char.IsWhiteSpace((char)Math.Min(c.Rune, 0xFFFF));
        var column = Math.Min(cell.Column, cells.Length - 1);
        if (!IsWordCell(cells[column]))
        {
            return;
        }

        int start = column, end = column + 1;
        while (start > 0 && IsWordCell(cells[start - 1]))
        {
            start--;
        }

        while (end < cells.Length && IsWordCell(cells[end]))
        {
            end++;
        }

        _selectionAnchor = (cell.Line, start);
        _selectionActive = (cell.Line, end);
    }

    /// <summary>The selected text; wrapped lines are joined, other line ends become newlines.</summary>
    private string? SelectedText()
    {
        if (!HasSelection || Buffer is not { } buffer)
        {
            return null;
        }

        var (start, end) = OrderedSelection();
        var builder = new System.Text.StringBuilder();
        for (var line = start.Line; line <= end.Line && line < buffer.TotalLines; line++)
        {
            var cells = buffer.LineAt(line).Cells;
            var from = line == start.Line ? Math.Min(start.Column, cells.Length) : 0;
            var to = line == end.Line ? Math.Min(end.Column, cells.Length) : cells.Length;
            var text = CellWidth.TextOf(cells[from..to]);
            var wrapped = buffer.LineAt(line).Wrapped;
            builder.Append(line == end.Line || wrapped ? text : text.TrimEnd());
            if (line != end.Line && !wrapped)
            {
                builder.Append(Environment.NewLine);
            }
        }

        return builder.ToString();
    }

    private ((int Line, int Column) Start, (int Line, int Column) End) OrderedSelection()
    {
        var anchor = _selectionAnchor!.Value;
        return anchor.Line < _selectionActive.Line || (anchor.Line == _selectionActive.Line && anchor.Column <= _selectionActive.Column)
            ? (anchor, _selectionActive)
            : (_selectionActive, anchor);
    }
}
