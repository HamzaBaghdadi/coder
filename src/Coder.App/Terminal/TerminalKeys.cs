using Avalonia.Input;

namespace Coder.App.Terminal;

/// <summary>
/// Translates key presses into the byte sequences a terminal sends (xterm conventions). Printable
/// characters are not handled here; they arrive as text input.
/// </summary>
internal static class TerminalKeys
{
    /// <summary>The sequence for a key, or null when the key produces text (or nothing).</summary>
    public static string? Encode(Key key, KeyModifiers modifiers, bool applicationCursorKeys)
    {
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);

        // xterm modifier parameter: 1 + shift(1) + alt(2) + ctrl(4).
        var modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        string Cursor(char final) => modifier > 1 ? $"\x1b[1;{modifier}{final}" : applicationCursorKeys ? $"\x1bO{final}" : $"\x1b[{final}";
        string Tilde(int code) => modifier > 1 ? $"\x1b[{code};{modifier}~" : $"\x1b[{code}~";

        var sequence = key switch
        {
            Key.Enter => "\r",
            Key.Back => ctrl ? "\x08" : "\x7f",
            Key.Tab => shift ? "\x1b[Z" : "\t",
            Key.Escape => "\x1b",
            Key.Up => Cursor('A'),
            Key.Down => Cursor('B'),
            Key.Right => Cursor('C'),
            Key.Left => Cursor('D'),
            Key.Home => Cursor('H'),
            Key.End => Cursor('F'),
            Key.Insert => Tilde(2),
            Key.Delete => Tilde(3),
            Key.PageUp => Tilde(5),
            Key.PageDown => Tilde(6),
            Key.F1 => "\x1bOP",
            Key.F2 => "\x1bOQ",
            Key.F3 => "\x1bOR",
            Key.F4 => "\x1bOS",
            Key.F5 => Tilde(15),
            Key.F6 => Tilde(17),
            Key.F7 => Tilde(18),
            Key.F8 => Tilde(19),
            Key.F9 => Tilde(20),
            Key.F10 => Tilde(21),
            Key.F11 => Tilde(23),
            Key.F12 => Tilde(24),
            _ => null,
        };

        if (sequence is not null)
        {
            return alt && sequence.Length == 1 ? "\x1b" + sequence : sequence;
        }

        // Ctrl+letter → control character (Ctrl+C = 0x03); Ctrl+Space → NUL.
        if (ctrl && !alt && key is >= Key.A and <= Key.Z)
        {
            return ((char)(key - Key.A + 1)).ToString();
        }

        if (ctrl && key == Key.Space)
        {
            return "\0";
        }

        // Alt+letter → ESC + letter (meta key), as readline and most shells expect.
        if (alt && !ctrl && key is >= Key.A and <= Key.Z)
        {
            var letter = (char)('a' + (key - Key.A));
            return "\x1b" + (shift ? char.ToUpperInvariant(letter) : letter);
        }

        return null;
    }
}
