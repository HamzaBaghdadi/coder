using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Coder.App.Theme;
using Coder.Core.Terminal;

namespace Coder.App.Terminal;

/// <summary>
/// Shell integration on screen, like VS Code's command decorations: a dot in the left margin beside
/// each command's prompt (an accent ring when it succeeded, a red dot when it failed, with the exit code
/// in its tooltip), and Ctrl+Up / Ctrl+Down to scroll to the previous or next command.
/// </summary>
public sealed partial class TerminalView
{
    private const double MarkRadius = 3.5;

    private static readonly Pen SucceededPen = new(Palette.Accent, 1.5);

    private void DrawCommandMarks(DrawingContext context, TerminalBuffer buffer, int top)
    {
        for (var row = 0; row < buffer.Rows; row++)
        {
            if (buffer.LineAt(top + row).Mark is not { ExitCode: not null } mark)
            {
                continue;
            }

            var center = new Point(Padding / 2, Padding / 2 + (row + 0.5) * _metrics.LineHeight);
            if (mark.Failed)
            {
                context.DrawEllipse(Palette.Danger, null, center, MarkRadius, MarkRadius);
            }
            else
            {
                context.DrawEllipse(null, SucceededPen, center, MarkRadius - 0.5, MarkRadius - 0.5);
            }
        }
    }

    /// <summary>The tooltip of the mark under the pointer in the left margin, or null.</summary>
    private string? CommandMarkTip(Point point)
    {
        if (point.X >= Padding || Buffer is not { } buffer)
        {
            return null;
        }

        var (line, _) = CellAt(point);
        return buffer.LineAt(line).Mark switch
        {
            { Failed: true } mark => $"Command failed (exit code {mark.ExitCode})",
            { Succeeded: true } => "Command succeeded",
            _ => null,
        };
    }

    /// <summary>Ctrl+Up / Ctrl+Down: the previous or next command's prompt comes to the top of the view.</summary>
    private bool TryScrollToCommand(KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control || e.Key is not (Key.Up or Key.Down)
            || Buffer is not { IsAlternateScreen: false } buffer || _session?.Emulator.HasShellIntegration != true)
        {
            return false;
        }

        var top = TopLine;
        var prompts = Enumerable.Range(0, buffer.TotalLines).Where(line => buffer.LineAt(line).Mark is not null).ToList();
        int? target = e.Key == Key.Up
            ? prompts.LastOrDefault(line => line < top, -1) is var up and >= 0 ? up : null
            : prompts.FirstOrDefault(line => line > top, -1) is var down and >= 0 ? down : null;
        if (target is { } line)
        {
            ScrollOffset = buffer.TotalLines - buffer.Rows - line;
        }

        return true;
    }

    /// <summary>For tests: the line at the top of the view.</summary>
    internal int TopVisibleLine => TopLine;

    /// <summary>For tests: a mark's tooltip at a point.</summary>
    internal string? CommandMarkTipAt(Point point) => CommandMarkTip(point);
}
