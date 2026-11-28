using System.Text;

namespace Coder.Core.Terminal;

/// <summary>
/// Builds the command line that starts a program with exact arguments, so a terminal can run a program
/// directly instead of typing a command into a shell (where quoting depends on the shell).
/// </summary>
public static class CommandLines
{
    /// <summary>
    /// A Windows command line for <paramref name="program"/> and <paramref name="arguments"/>, quoted so that
    /// the program receives each argument unchanged (the rules of CommandLineToArgvW and the C runtime).
    /// </summary>
    public static string Windows(string program, IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder(program.Contains(' ', StringComparison.Ordinal) || program.Contains('\t', StringComparison.Ordinal) ? $"\"{program}\"" : program);
        foreach (var argument in arguments)
        {
            line.Append(' ').Append(QuoteWindows(argument));
        }

        return line.ToString();
    }

    /// <summary>One argument: as is when it has no spaces, tabs or quotes; else quoted, with quotes and the backslashes before them escaped.</summary>
    public static string QuoteWindows(string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes count only before a quote: double them there, and escape the quote.
            quoted.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString(); // before the closing quote too
    }
}
