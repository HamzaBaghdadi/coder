namespace Coder.Core.Terminal;

/// <summary>A row of cells. <see cref="Version"/> changes on every edit so renderers can cache rows.</summary>
public sealed class TerminalLine(int columns, CellStyle style)
{
    public TerminalCell[] Cells { get; private set; } = Enumerable.Repeat(TerminalCell.Blank(style), columns).ToArray();

    /// <summary>True when the text continues on the next line because it hit the right margin.</summary>
    public bool Wrapped { get; set; }

    public int Version { get; private set; }

    public void Set(int column, TerminalCell cell)
    {
        Cells[column] = cell;
        Version++;
    }

    public void Fill(int from, int to, TerminalCell cell)
    {
        Array.Fill(Cells, cell, from, Math.Max(0, to - from));
        Version++;
    }

    public void Resize(int columns, CellStyle style)
    {
        if (columns == Cells.Length)
        {
            return;
        }

        var old = Cells;
        Cells = new TerminalCell[columns];
        Array.Copy(old, Cells, Math.Min(old.Length, columns));
        Array.Fill(Cells, TerminalCell.Blank(style), old.Length < columns ? old.Length : columns, Math.Max(0, columns - old.Length));
        Version++;
    }

    /// <summary>Shifts cells right from <paramref name="column"/> (insert) or left (negative count, delete).</summary>
    public void Shift(int column, int count, TerminalCell fill)
    {
        if (count > 0)
        {
            Array.Copy(Cells, column, Cells, column + count, Math.Max(0, Cells.Length - column - count));
            Array.Fill(Cells, fill, column, Math.Min(count, Cells.Length - column));
        }
        else
        {
            var n = -count;
            Array.Copy(Cells, column + n, Cells, column, Math.Max(0, Cells.Length - column - n));
            Array.Fill(Cells, fill, Math.Max(column, Cells.Length - n), Math.Min(n, Cells.Length - column));
        }

        Version++;
    }

    public string Text => CellWidth.TextOf(Cells).TrimEnd();

    /// <summary>Set on the line where a prompt starts, when the shell reports its commands (shell integration).</summary>
    public CommandMark? Mark { get; set; }
}

/// <summary>
/// A command the shell reported (shell integration): its prompt's line carries this mark. The exit code
/// is known once the command finished; it stays null for a prompt still waiting, or when nothing ran.
/// </summary>
public sealed class CommandMark
{
    public int? ExitCode { get; set; }

    public bool Succeeded => ExitCode == 0;

    public bool Failed => ExitCode is not null and not 0;
}

/// <summary>
/// The terminal's screen: a grid of <see cref="Rows"/> × <see cref="Columns"/> cells, the cursor, the
/// scroll region, and the scrollback above the screen. It offers the primitive operations escape
/// sequences are made of; <see cref="TerminalEmulator"/> decides which ones to call.
/// </summary>
public sealed class TerminalBuffer
{
    public const int DefaultScrollbackLimit = 5_000;

    private readonly List<TerminalLine> _scrollback = [];
    private readonly int _scrollbackLimit;
    private TerminalLine[] _screen;
    private (TerminalLine[] Screen, int Row, int Column)? _mainScreen; // saved while the alternate screen is shown
    private (int Row, int Column, CellStyle Style) _savedCursor;
    private bool _pendingWrap;

    public TerminalBuffer(int columns, int rows, int scrollbackLimit = DefaultScrollbackLimit)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        _scrollbackLimit = scrollbackLimit;
        _screen = NewScreen(Columns, Rows);
        ScrollBottom = Rows - 1;
    }

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public int CursorRow { get; private set; }

    public int CursorColumn { get; private set; }

    public bool CursorVisible { get; set; } = true;

    /// <summary>Wrap to the next line at the right margin (DECAWM).</summary>
    public bool AutoWrap { get; set; } = true;

    public CellStyle Style { get; set; }

    public int ScrollTop { get; private set; }

    public int ScrollBottom { get; private set; }

    public bool IsAlternateScreen => _mainScreen is not null;

    public int ScrollbackCount => _scrollback.Count;

    /// <summary>Scrollback lines followed by screen lines.</summary>
    public int TotalLines => _scrollback.Count + Rows;

    /// <summary>A line by absolute index (0 = oldest scrollback line).</summary>
    public TerminalLine LineAt(int index) => index < _scrollback.Count ? _scrollback[index] : _screen[index - _scrollback.Count];

    public TerminalLine ScreenLine(int row) => _screen[row];

    // Writing ----------------------------------------------------------------------------------------

    /// <summary>Writes a character at the cursor and advances it, wrapping at the right margin.</summary>
    public void Print(int rune)
    {
        var width = CellWidth.Of(rune);
        if (width == 0)
        {
            return; // combining marks and format characters are not drawn on their own
        }

        if (_pendingWrap || (width == 2 && CursorColumn == Columns - 1))
        {
            if (AutoWrap)
            {
                _screen[CursorRow].Wrapped = true;
                CarriageReturn();
                LineFeed();
            }

            _pendingWrap = false;
        }

        var line = _screen[CursorRow];
        line.Set(CursorColumn, new TerminalCell(rune, Style, (byte)width));
        if (width == 2 && CursorColumn + 1 < Columns)
        {
            line.Set(CursorColumn + 1, new TerminalCell(0, Style, 0));
        }

        if (CursorColumn + width >= Columns)
        {
            CursorColumn = Columns - 1;
            _pendingWrap = true; // the next character wraps first
        }
        else
        {
            CursorColumn += width;
        }
    }

    public void CarriageReturn()
    {
        CursorColumn = 0;
        _pendingWrap = false;
    }

    /// <summary>Moves down a line, scrolling the region when at its bottom.</summary>
    public void LineFeed()
    {
        _pendingWrap = false;
        if (CursorRow == ScrollBottom)
        {
            ScrollUp(1);
        }
        else if (CursorRow < Rows - 1)
        {
            CursorRow++;
        }
    }

    /// <summary>Moves up a line, scrolling the region down when at its top (RI).</summary>
    public void ReverseLineFeed()
    {
        _pendingWrap = false;
        if (CursorRow == ScrollTop)
        {
            ScrollDown(1);
        }
        else if (CursorRow > 0)
        {
            CursorRow--;
        }
    }

    public void Backspace()
    {
        _pendingWrap = false;
        CursorColumn = Math.Max(0, CursorColumn - 1);
    }

    public void Tab(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            CursorColumn = Math.Min(Columns - 1, (CursorColumn / 8 + 1) * 8);
        }
    }

    public void BackTab(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            CursorColumn = Math.Max(0, (CursorColumn - 1) / 8 * 8);
        }
    }

    // Cursor -----------------------------------------------------------------------------------------

    public void MoveCursorTo(int row, int column)
    {
        _pendingWrap = false;
        CursorRow = Math.Clamp(row, 0, Rows - 1);
        CursorColumn = Math.Clamp(column, 0, Columns - 1);
    }

    /// <summary>Relative move; vertical moves stop at the scroll region edges when starting inside it.</summary>
    public void MoveCursorBy(int rows, int columns)
    {
        var top = CursorRow >= ScrollTop ? ScrollTop : 0;
        var bottom = CursorRow <= ScrollBottom ? ScrollBottom : Rows - 1;
        _pendingWrap = false;
        CursorRow = Math.Clamp(CursorRow + rows, top, bottom);
        CursorColumn = Math.Clamp(CursorColumn + columns, 0, Columns - 1);
    }

    public void SaveCursor() => _savedCursor = (CursorRow, CursorColumn, Style);

    public void RestoreCursor()
    {
        MoveCursorTo(_savedCursor.Row, _savedCursor.Column);
        Style = _savedCursor.Style;
    }

    // Scrolling and regions --------------------------------------------------------------------------

    public void SetScrollRegion(int top, int bottom)
    {
        if (top >= bottom || bottom >= Rows)
        {
            (top, bottom) = (0, Rows - 1);
        }

        ScrollTop = top;
        ScrollBottom = bottom;
        MoveCursorTo(0, 0);
    }

    /// <summary>Scrolls the region up; lines leaving a full-screen main region go to the scrollback.</summary>
    public void ScrollUp(int count)
    {
        count = Math.Min(count, ScrollBottom - ScrollTop + 1);
        for (var i = 0; i < count; i++)
        {
            var leaving = _screen[ScrollTop];
            if (ScrollTop == 0 && !IsAlternateScreen)
            {
                AddToScrollback(leaving);
            }

            Array.Copy(_screen, ScrollTop + 1, _screen, ScrollTop, ScrollBottom - ScrollTop);
            _screen[ScrollBottom] = new TerminalLine(Columns, Style);
        }
    }

    public void ScrollDown(int count)
    {
        count = Math.Min(count, ScrollBottom - ScrollTop + 1);
        for (var i = 0; i < count; i++)
        {
            Array.Copy(_screen, ScrollTop, _screen, ScrollTop + 1, ScrollBottom - ScrollTop);
            _screen[ScrollTop] = new TerminalLine(Columns, Style);
        }
    }

    /// <summary>IL: inserts blank lines at the cursor, pushing lines below it down within the region.</summary>
    public void InsertLines(int count)
    {
        if (CursorRow < ScrollTop || CursorRow > ScrollBottom)
        {
            return;
        }

        var top = ScrollTop;
        ScrollTop = CursorRow;
        ScrollDown(count);
        ScrollTop = top;
        CursorColumn = 0;
    }

    /// <summary>DL: deletes lines at the cursor, pulling lines below it up within the region.</summary>
    public void DeleteLines(int count)
    {
        if (CursorRow < ScrollTop || CursorRow > ScrollBottom)
        {
            return;
        }

        var top = ScrollTop;
        ScrollTop = CursorRow;
        var lines = Math.Min(count, ScrollBottom - ScrollTop + 1);
        for (var i = 0; i < lines; i++)
        {
            Array.Copy(_screen, ScrollTop + 1, _screen, ScrollTop, ScrollBottom - ScrollTop);
            _screen[ScrollBottom] = new TerminalLine(Columns, Style);
        }

        ScrollTop = top;
        CursorColumn = 0;
    }

    // Erasing ----------------------------------------------------------------------------------------

    /// <summary>ED: 0 = cursor to end, 1 = start to cursor, 2 = screen, 3 = screen and scrollback.</summary>
    public void EraseInDisplay(int mode)
    {
        var blank = TerminalCell.Blank(Style);
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (var row = CursorRow + 1; row < Rows; row++)
                {
                    _screen[row].Fill(0, Columns, blank);
                }

                break;
            case 1:
                EraseInLine(1);
                for (var row = 0; row < CursorRow; row++)
                {
                    _screen[row].Fill(0, Columns, blank);
                }

                break;
            case 2:
            case 3:
                foreach (var line in _screen)
                {
                    line.Fill(0, Columns, blank);
                    line.Wrapped = false;
                }

                if (mode == 3)
                {
                    _scrollback.Clear();
                }

                break;
        }
    }

    /// <summary>EL: 0 = cursor to end of line, 1 = start of line to cursor, 2 = whole line.</summary>
    public void EraseInLine(int mode)
    {
        var line = _screen[CursorRow];
        var blank = TerminalCell.Blank(Style);
        switch (mode)
        {
            case 0:
                line.Fill(CursorColumn, Columns, blank);
                line.Wrapped = false;
                break;
            case 1:
                line.Fill(0, CursorColumn + 1, blank);
                break;
            case 2:
                line.Fill(0, Columns, blank);
                line.Wrapped = false;
                break;
        }
    }

    /// <summary>ECH: blanks characters from the cursor without moving anything.</summary>
    public void EraseCharacters(int count) =>
        _screen[CursorRow].Fill(CursorColumn, Math.Min(Columns, CursorColumn + count), TerminalCell.Blank(Style));

    /// <summary>ICH: inserts blank cells at the cursor, shifting the rest of the line right.</summary>
    public void InsertCharacters(int count) => _screen[CursorRow].Shift(CursorColumn, Math.Min(count, Columns - CursorColumn), TerminalCell.Blank(Style));

    /// <summary>DCH: deletes cells at the cursor, shifting the rest of the line left.</summary>
    public void DeleteCharacters(int count) => _screen[CursorRow].Shift(CursorColumn, -Math.Min(count, Columns - CursorColumn), TerminalCell.Blank(Style));

    // Screens and size -------------------------------------------------------------------------------

    /// <summary>Full-screen programs (vim, less, htop) draw on a separate screen without scrollback.</summary>
    public void SetAlternateScreen(bool enabled)
    {
        if (enabled == IsAlternateScreen)
        {
            return;
        }

        if (enabled)
        {
            _mainScreen = (_screen, CursorRow, CursorColumn);
            _screen = NewScreen(Columns, Rows);
            MoveCursorTo(0, 0);
        }
        else
        {
            var main = _mainScreen!.Value;
            _mainScreen = null;
            _screen = FitScreen(main.Screen, Columns, Rows);
            MoveCursorTo(main.Row, main.Column);
        }

        SetScrollRegion(0, Rows - 1);
    }

    /// <summary>Resizes the grid. Lines are cut or padded (no rewrapping); rows leaving the top go to the scrollback.</summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        // Keep the cursor on screen: when shrinking, move the top lines into the scrollback.
        var overflow = Math.Max(0, CursorRow + 1 - rows);
        for (var i = 0; i < overflow; i++)
        {
            if (!IsAlternateScreen)
            {
                AddToScrollback(_screen[i]);
            }
        }

        var kept = _screen.Skip(overflow).ToArray();
        Columns = columns;
        Rows = rows;
        _screen = FitScreen(kept, columns, rows);
        foreach (var line in _scrollback)
        {
            line.Resize(columns, CellStyle.Default);
        }

        if (_mainScreen is { } main)
        {
            _mainScreen = (FitScreen(main.Screen, columns, rows), Math.Min(main.Row, rows - 1), Math.Min(main.Column, columns - 1));
        }

        ScrollTop = 0;
        ScrollBottom = rows - 1;
        MoveCursorTo(CursorRow - overflow, CursorColumn);
    }

    /// <summary>RIS: back to a blank screen and default state, keeping the size.</summary>
    public void Reset()
    {
        _mainScreen = null;
        _scrollback.Clear();
        Style = CellStyle.Default;
        _screen = NewScreen(Columns, Rows);
        CursorVisible = true;
        AutoWrap = true;
        SetScrollRegion(0, Rows - 1);
    }

    private void AddToScrollback(TerminalLine line)
    {
        _scrollback.Add(line);
        if (_scrollback.Count > _scrollbackLimit)
        {
            _scrollback.RemoveRange(0, _scrollback.Count - _scrollbackLimit);
        }
    }

    private static TerminalLine[] NewScreen(int columns, int rows) =>
        Enumerable.Range(0, rows).Select(_ => new TerminalLine(columns, CellStyle.Default)).ToArray();

    private static TerminalLine[] FitScreen(TerminalLine[] lines, int columns, int rows)
    {
        var screen = new TerminalLine[rows];
        for (var row = 0; row < rows; row++)
        {
            var line = row < lines.Length ? lines[row] : new TerminalLine(columns, CellStyle.Default);
            line.Resize(columns, CellStyle.Default);
            screen[row] = line;
        }

        return screen;
    }
}
