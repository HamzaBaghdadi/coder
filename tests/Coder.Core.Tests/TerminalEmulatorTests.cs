using Coder.Core.Terminal;

namespace Coder.Core.Tests;

public sealed class TerminalEmulatorTests
{
    [Fact]
    public void Text_and_line_breaks_fill_the_screen()
    {
        var terminal = Feed(20, 5, "hello\r\nworld");

        Assert.Equal("hello", Line(terminal, 0));
        Assert.Equal("world", Line(terminal, 1));
        Assert.Equal((1, 5), (terminal.Buffer.CursorRow, terminal.Buffer.CursorColumn));
    }

    [Fact]
    public void Long_lines_wrap_at_the_right_margin()
    {
        var terminal = Feed(5, 3, "abcdefg");

        Assert.Equal("abcde", Line(terminal, 0));
        Assert.True(terminal.Buffer.ScreenLine(0).Wrapped);
        Assert.Equal("fg", Line(terminal, 1));
    }

    [Fact]
    public void Output_past_the_bottom_scrolls_into_the_scrollback()
    {
        var terminal = Feed(10, 2, "1\r\n2\r\n3\r\n4");

        Assert.Equal(2, terminal.Buffer.ScrollbackCount);
        Assert.Equal("1", terminal.Buffer.LineAt(0).Text);
        Assert.Equal("3", Line(terminal, 0));
        Assert.Equal("4", Line(terminal, 1));
    }

    [Fact]
    public void Cursor_moves_and_erases_like_a_shell_redrawing_its_prompt()
    {
        // Typical prompt redraw: move home, write, erase the rest of the line.
        var terminal = Feed(20, 3, "old prompt text\x1b[1;1Hnew\x1b[K");

        Assert.Equal("new", Line(terminal, 0));
    }

    [Fact]
    public void Colors_cover_16_256_and_24_bit_forms()
    {
        var terminal = Feed(20, 2, "\x1b[1;31mA\x1b[38;5;208mB\x1b[48;2;10;20;30mC\x1b[0mD");
        var cells = terminal.Buffer.ScreenLine(0).Cells;

        Assert.Equal(TerminalColor.Indexed(1), cells[0].Style.Foreground);
        Assert.True(cells[0].Style.Attributes.HasFlag(CellAttributes.Bold));
        Assert.Equal(TerminalColor.Indexed(208), cells[1].Style.Foreground);
        Assert.Equal(TerminalColor.Rgb(10, 20, 30), cells[2].Style.Background);
        Assert.Equal(CellStyle.Default, cells[3].Style);
    }

    [Fact]
    public void Alternate_screen_is_separate_and_restores_the_main_screen()
    {
        var terminal = Feed(20, 3, "shell$ \x1b[?1049hFULLSCREEN APP");
        Assert.Equal("FULLSCREEN APP", Line(terminal, 0));

        terminal.Feed("\x1b[?1049l");
        Assert.Equal("shell$", Line(terminal, 0));
        Assert.Equal(7, terminal.Buffer.CursorColumn);
    }

    [Fact]
    public void Wide_characters_take_two_cells()
    {
        var terminal = Feed(10, 2, "a你b");
        var cells = terminal.Buffer.ScreenLine(0).Cells;

        Assert.Equal(2, cells[1].Width);
        Assert.True(cells[2].IsContinuation);
        Assert.Equal('b', cells[3].Rune);
        Assert.Equal("a你b", Line(terminal, 0));
    }

    [Fact]
    public void Sequences_split_across_chunks_are_still_recognized()
    {
        var terminal = new TerminalEmulator(20, 2);
        foreach (var chunk in new[] { "ab\x1b", "[3", "1mc", "\x1b]0;my ti", "tle\x07" })
        {
            terminal.Feed(chunk);
        }

        Assert.Equal("abc", Line(terminal, 0));
        Assert.Equal(TerminalColor.Indexed(1), terminal.Buffer.ScreenLine(0).Cells[2].Style.Foreground);
    }

    [Fact]
    public void Cursor_position_query_gets_an_answer()
    {
        var terminal = new TerminalEmulator(20, 5);
        string? answer = null;
        terminal.Response += (_, text) => answer = text;

        terminal.Feed("ab\r\nc\x1b[6n");

        Assert.Equal("\x1b[2;2R", answer);
    }

    [Fact]
    public void Scroll_region_keeps_lines_outside_it_fixed()
    {
        var terminal = Feed(10, 4, "top\x1b[2;3r\x1b[3;1Hx\r\ny\r\nz");

        Assert.Equal("top", Line(terminal, 0));
        Assert.Equal("y", Line(terminal, 1));
        Assert.Equal("z", Line(terminal, 2));
        Assert.Equal(0, terminal.Buffer.ScrollbackCount);
    }

    private static TerminalEmulator Feed(int columns, int rows, string output)
    {
        var terminal = new TerminalEmulator(columns, rows);
        terminal.Feed(output);
        return terminal;
    }

    private static string Line(TerminalEmulator terminal, int row) => terminal.Buffer.ScreenLine(row).Text;
}
