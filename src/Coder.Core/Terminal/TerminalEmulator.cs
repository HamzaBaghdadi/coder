namespace Coder.Core.Terminal;

/// <summary>
/// An xterm-compatible terminal: feed it a program's output and read the resulting screen from
/// <see cref="Buffer"/>. Covers what shells and common full-screen programs use: cursor movement,
/// erasing, scroll regions, insert/delete, 16/256/24-bit colors, the alternate screen, and replies to
/// status queries. Unknown sequences are ignored.
/// </summary>
public sealed class TerminalEmulator : IVtHandler
{
    private readonly VtParser _parser;

    public TerminalEmulator(int columns, int rows)
    {
        Buffer = new TerminalBuffer(columns, rows);
        _parser = new VtParser(this);
    }

    /// <summary>Text the terminal must send back to the program (answers to status queries).</summary>
    public event EventHandler<string>? Response;

    /// <summary>Raised when the program sets the window title (OSC 0 / 2).</summary>
    public event EventHandler<string>? TitleChanged;

    public TerminalBuffer Buffer { get; }

    /// <summary>Arrow keys send ESC O x instead of ESC [ x (DECCKM), used by full-screen programs.</summary>
    public bool ApplicationCursorKeys { get; private set; }

    /// <summary>Pasted text should be wrapped in ESC [200~ … ESC [201~ so the shell does not run it line by line.</summary>
    public bool BracketedPaste { get; private set; }

    public void Feed(ReadOnlySpan<char> output) => _parser.Feed(output);

    public void Resize(int columns, int rows) => Buffer.Resize(columns, rows);

    void IVtHandler.Print(int rune) => Buffer.Print(rune);

    void IVtHandler.Execute(char control)
    {
        switch (control)
        {
            case '\n' or '\v' or '\f':
                Buffer.LineFeed();
                break;
            case '\r':
                Buffer.CarriageReturn();
                break;
            case '\b':
                Buffer.Backspace();
                break;
            case '\t':
                Buffer.Tab();
                break;
        }
    }

    void IVtHandler.EscDispatch(string intermediates, char final)
    {
        if (intermediates.Length > 0)
        {
            return; // character set selection and the like: not needed for UTF-8 output
        }

        switch (final)
        {
            case '7':
                Buffer.SaveCursor();
                break;
            case '8':
                Buffer.RestoreCursor();
                break;
            case 'D':
                Buffer.LineFeed();
                break;
            case 'E':
                Buffer.CarriageReturn();
                Buffer.LineFeed();
                break;
            case 'M':
                Buffer.ReverseLineFeed();
                break;
            case 'c':
                Buffer.Reset();
                ApplicationCursorKeys = false;
                BracketedPaste = false;
                break;
        }
    }

    void IVtHandler.OscDispatch(string command)
    {
        var separator = command.IndexOf(';', StringComparison.Ordinal);
        var code = separator > 0 ? command[..separator] : command;
        var rest = separator > 0 ? command[(separator + 1)..] : string.Empty;
        switch (code)
        {
            case "0" or "2":
                TitleChanged?.Invoke(this, rest);
                break;
            case "633":
                ShellIntegration(rest);
                break;
            case "7" when Uri.TryCreate(rest, UriKind.Absolute, out var uri) && uri.IsFile:
                // OSC 7: file://host/path. The host is this machine; a Windows path comes as /C:/folder.
                var path = Uri.UnescapeDataString(uri.AbsolutePath);
                SetCurrentDirectory(path.Length > 2 && path[0] == '/' && path[2] == ':' ? path[1..].Replace('/', '\\') : path);
                break;
            case "9" when rest.StartsWith("9;", StringComparison.Ordinal):
                SetCurrentDirectory(rest[2..].Trim('"')); // OSC 9;9: Windows Terminal's form
                break;
        }
    }

    /// <summary>The folder the shell is in, when it reports it (shell integration, OSC 7 or OSC 9;9).</summary>
    public string? CurrentDirectory { get; private set; }

    /// <summary>A command finished and the shell reported its exit code.</summary>
    public event EventHandler<CommandMark>? CommandFinished;

    /// <summary>Whether the shell reports its prompts and commands (it sent a shell integration sequence).</summary>
    public bool HasShellIntegration { get; private set; }

    private CommandMark? _lastPrompt;

    /// <summary>
    /// OSC 633 (VS Code's shell integration): A = a prompt starts here, B = the command line starts,
    /// C = the command runs, D[;exit code] = it finished, P;Cwd=… = the current folder.
    /// </summary>
    private void ShellIntegration(string sequence)
    {
        HasShellIntegration = true;
        var kind = sequence.Length > 0 ? sequence[0] : '\0';
        switch (kind)
        {
            case 'A':
                _lastPrompt = new CommandMark();
                Buffer.ScreenLine(Buffer.CursorRow).Mark = _lastPrompt;
                break;
            case 'D' when _lastPrompt is { } prompt:
                var parts = sequence.Split(';');
                if (parts.Length > 1 && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var exitCode))
                {
                    prompt.ExitCode = exitCode;
                    CommandFinished?.Invoke(this, prompt);
                }

                break;
            case 'P' when sequence.StartsWith("P;Cwd=", StringComparison.Ordinal):
                SetCurrentDirectory(sequence["P;Cwd=".Length..]);
                break;
        }
    }

    private void SetCurrentDirectory(string path)
    {
        if (path.Length > 0)
        {
            CurrentDirectory = path;
        }
    }

    void IVtHandler.CsiDispatch(IReadOnlyList<int> p, char privateMarker, string intermediates, char final)
    {
        if (privateMarker == '?')
        {
            SetDecModes(p, final);
            return;
        }

        if (privateMarker != '\0' || intermediates.Length > 0)
        {
            return;
        }

        var b = Buffer;
        var n = Param(p, 0, 1);
        switch (final)
        {
            case 'A': b.MoveCursorBy(-n, 0); break;
            case 'B' or 'e': b.MoveCursorBy(n, 0); break;
            case 'C' or 'a': b.MoveCursorBy(0, n); break;
            case 'D': b.MoveCursorBy(0, -n); break;
            case 'E': b.MoveCursorBy(n, 0); b.CarriageReturn(); break;
            case 'F': b.MoveCursorBy(-n, 0); b.CarriageReturn(); break;
            case 'G' or '`': b.MoveCursorTo(b.CursorRow, n - 1); break;
            case 'd': b.MoveCursorTo(n - 1, b.CursorColumn); break;
            case 'H' or 'f': b.MoveCursorTo(Param(p, 0, 1) - 1, Param(p, 1, 1) - 1); break;
            case 'J': b.EraseInDisplay(Param(p, 0, 0)); break;
            case 'K': b.EraseInLine(Param(p, 0, 0)); break;
            case 'L': b.InsertLines(n); break;
            case 'M': b.DeleteLines(n); break;
            case '@': b.InsertCharacters(n); break;
            case 'P': b.DeleteCharacters(n); break;
            case 'X': b.EraseCharacters(n); break;
            case 'S': b.ScrollUp(n); break;
            case 'T': b.ScrollDown(n); break;
            case 'I': b.Tab(n); break;
            case 'Z': b.BackTab(n); break;
            case 'r': b.SetScrollRegion(Param(p, 0, 1) - 1, Param(p, 1, b.Rows) - 1); break;
            case 's': b.SaveCursor(); break;
            case 'u': b.RestoreCursor(); break;
            case 'm': SetGraphicsRendition(p); break;
            case 'n': ReportStatus(Param(p, 0, 0)); break;
            case 'c': Response?.Invoke(this, "\x1b[?1;2c"); break; // "VT100 with advanced video"
        }
    }

    /// <summary>The <paramref name="index"/>-th parameter, or <paramref name="fallback"/> when missing or 0.</summary>
    private static int Param(IReadOnlyList<int> parameters, int index, int fallback) =>
        index < parameters.Count && parameters[index] > 0 ? parameters[index] : fallback;

    private void ReportStatus(int request)
    {
        switch (request)
        {
            case 5:
                Response?.Invoke(this, "\x1b[0n"); // OK
                break;
            case 6:
                Response?.Invoke(this, $"\x1b[{Buffer.CursorRow + 1};{Buffer.CursorColumn + 1}R");
                break;
        }
    }

    private void SetDecModes(IReadOnlyList<int> parameters, char final)
    {
        if (final is not ('h' or 'l'))
        {
            return;
        }

        var enable = final == 'h';
        foreach (var mode in parameters)
        {
            switch (mode)
            {
                case 1:
                    ApplicationCursorKeys = enable;
                    break;
                case 7:
                    Buffer.AutoWrap = enable;
                    break;
                case 25:
                    Buffer.CursorVisible = enable;
                    break;
                case 47 or 1047:
                    Buffer.SetAlternateScreen(enable);
                    break;
                case 1049:
                    if (enable)
                    {
                        Buffer.SaveCursor();
                        Buffer.SetAlternateScreen(true);
                    }
                    else
                    {
                        Buffer.SetAlternateScreen(false);
                        Buffer.RestoreCursor();
                    }

                    break;
                case 2004:
                    BracketedPaste = enable;
                    break;
            }
        }
    }

    /// <summary>SGR: colors and text attributes for the characters that follow.</summary>
    private void SetGraphicsRendition(IReadOnlyList<int> p)
    {
        var style = Buffer.Style;
        for (var i = 0; i < p.Count; i++)
        {
            var code = Math.Max(0, p[i]);
            switch (code)
            {
                case 0: style = CellStyle.Default; break;
                case 1: style = style with { Attributes = style.Attributes | CellAttributes.Bold }; break;
                case 2: style = style with { Attributes = style.Attributes | CellAttributes.Dim }; break;
                case 3: style = style with { Attributes = style.Attributes | CellAttributes.Italic }; break;
                case 4: style = style with { Attributes = style.Attributes | CellAttributes.Underline }; break;
                case 7: style = style with { Attributes = style.Attributes | CellAttributes.Inverse }; break;
                case 8: style = style with { Attributes = style.Attributes | CellAttributes.Hidden }; break;
                case 9: style = style with { Attributes = style.Attributes | CellAttributes.Strikethrough }; break;
                case 21 or 22: style = style with { Attributes = style.Attributes & ~(CellAttributes.Bold | CellAttributes.Dim) }; break;
                case 23: style = style with { Attributes = style.Attributes & ~CellAttributes.Italic }; break;
                case 24: style = style with { Attributes = style.Attributes & ~CellAttributes.Underline }; break;
                case 27: style = style with { Attributes = style.Attributes & ~CellAttributes.Inverse }; break;
                case 28: style = style with { Attributes = style.Attributes & ~CellAttributes.Hidden }; break;
                case 29: style = style with { Attributes = style.Attributes & ~CellAttributes.Strikethrough }; break;
                case >= 30 and <= 37: style = style with { Foreground = TerminalColor.Indexed(code - 30) }; break;
                case 38: style = style with { Foreground = ExtendedColor(p, ref i) }; break;
                case 39: style = style with { Foreground = TerminalColor.Default }; break;
                case >= 40 and <= 47: style = style with { Background = TerminalColor.Indexed(code - 40) }; break;
                case 48: style = style with { Background = ExtendedColor(p, ref i) }; break;
                case 49: style = style with { Background = TerminalColor.Default }; break;
                case >= 90 and <= 97: style = style with { Foreground = TerminalColor.Indexed(code - 90 + 8) }; break;
                case >= 100 and <= 107: style = style with { Background = TerminalColor.Indexed(code - 100 + 8) }; break;
            }
        }

        Buffer.Style = style;
    }

    /// <summary>Reads "5;n" (palette) or "2;r;g;b" (24-bit) after a 38/48, advancing <paramref name="i"/>.</summary>
    private static TerminalColor ExtendedColor(IReadOnlyList<int> p, ref int i)
    {
        if (i + 2 < p.Count && p[i + 1] == 5)
        {
            i += 2;
            return TerminalColor.Indexed(Math.Clamp(p[i], 0, 255));
        }

        if (i + 4 < p.Count && p[i + 1] == 2)
        {
            var color = TerminalColor.Rgb(Math.Clamp(p[i + 2], 0, 255), Math.Clamp(p[i + 3], 0, 255), Math.Clamp(p[i + 4], 0, 255));
            i += 4;
            return color;
        }

        return TerminalColor.Default;
    }
}
