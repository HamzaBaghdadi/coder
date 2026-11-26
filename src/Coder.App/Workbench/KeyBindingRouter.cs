using Avalonia.Input;
using Coder.Presentation.Commands;

namespace Coder.App.Workbench;

/// <summary>
/// Maps global key gestures to registered commands. Gestures come from the command definitions, so
/// the palette shows exactly the shortcut that works. A binding can be a chord of two gestures, like
/// VS Code's "Ctrl+K Ctrl+T": the first one waits for the second.
/// </summary>
public sealed class KeyBindingRouter
{
    /// <summary>
    /// While the terminal has focus, keys belong to the shell (Ctrl+B, Ctrl+F, Ctrl+W… all mean
    /// something there). Only these app commands keep their shortcuts, like VS Code's defaults.
    /// </summary>
    private static readonly HashSet<string> AvailableInTerminal =
    [
        CommandIds.CommandPalette,
        CommandIds.QuickOpen,
        CommandIds.ToggleTerminal,
        CommandIds.NewTerminal,
        CommandIds.SplitTerminal,
    ];

    private readonly List<(KeyGesture[] Keys, CommandDefinition Command)> _bindings;
    private readonly Action<string> _showMessage;
    private KeyGesture? _chordStart;

    /// <param name="showMessage">Tells the user a chord is waiting for its second key, or didn't match.</param>
    public KeyBindingRouter(CommandRegistry commands, Action<string>? showMessage = null)
    {
        _showMessage = showMessage ?? (_ => { });
        _bindings = commands.All
            .Where(command => command.KeyBinding is not null)
            .Select(command => (command.KeyBinding!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(KeyGesture.Parse).ToArray(), command))
            .ToList();
    }

    /// <summary>Runs the command bound to the pressed keys. Returns true when the keys were used.</summary>
    public bool TryExecute(KeyEventArgs e, bool terminalHasFocus)
    {
        if (IsModifier(e.Key))
        {
            return false; // pressing Ctrl on the way to the chord's second key
        }

        var available = _bindings.Where(b => !terminalHasFocus || AvailableInTerminal.Contains(b.Command.Id)).ToList();
        if (_chordStart is { } start)
        {
            _chordStart = null;
            var second = available.FirstOrDefault(b => b.Keys.Length == 2 && b.Keys[0].Equals(start) && b.Keys[1].Matches(e));
            if (second.Command is null)
            {
                _showMessage($"The key combination ({start}, {e.KeyModifiers}+{e.Key}) is not a command.");
                return true;
            }

            _showMessage(string.Empty);
            _ = second.Command.Execute();
            return true;
        }

        var single = available.FirstOrDefault(b => b.Keys.Length == 1 && b.Keys[0].Matches(e));
        if (single.Command is not null)
        {
            _ = single.Command.Execute();
            return true;
        }

        var chord = available.FirstOrDefault(b => b.Keys.Length == 2 && b.Keys[0].Matches(e));
        if (chord.Command is not null)
        {
            _chordStart = chord.Keys[0];
            _showMessage($"({chord.Keys[0]}) was pressed. Waiting for second key of chord…");
            return true;
        }

        return false;
    }

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
