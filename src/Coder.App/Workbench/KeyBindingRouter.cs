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

    private readonly CommandRegistry _commands;
    private readonly Action<string> _showMessage;
    private List<(KeyGesture[] Keys, CommandDefinition Command)> _bindings = [];
    private KeyGesture? _chordStart;

    /// <param name="showMessage">Tells the user a chord is waiting for its second key, or didn't match.</param>
    public KeyBindingRouter(CommandRegistry commands, Action<string>? showMessage = null)
    {
        _commands = commands;
        _showMessage = showMessage ?? (_ => { });
        Rebuild();
        commands.Changed += (_, _) => Rebuild(); // extensions add and remove commands while Coder runs
    }

    /// <summary>Reads every command's binding; one an extension wrote that Avalonia can't parse is left out.</summary>
    private void Rebuild()
    {
        var bindings = new List<(KeyGesture[] Keys, CommandDefinition Command)>();
        foreach (var command in _commands.All.Where(c => c.KeyBinding is not null))
        {
            try
            {
                bindings.Add((command.KeyBinding!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(KeyGesture.Parse).ToArray(), command));
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                // a key Coder doesn't know: the command still runs from the palette
            }
        }

        _bindings = bindings;
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
