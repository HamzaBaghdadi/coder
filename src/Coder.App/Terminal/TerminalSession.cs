using System.Text;
using Avalonia.Threading;
using Coder.Core.Terminal;

namespace Coder.App.Terminal;

/// <summary>
/// One running shell: the pseudo-terminal it runs in, plus the emulator that turns its output into a
/// screen. Output arrives on a background thread; it is batched and applied on the UI thread at most
/// once per frame, so a flood of output (a big build log) never blocks typing or drawing.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private readonly IPseudoTerminal _terminal;
    private readonly StringBuilder _pending = new();
    private readonly Lock _pendingLock = new();
    private bool _flushScheduled;

    private TerminalSession(IPseudoTerminal terminal, TerminalEmulator emulator)
    {
        _terminal = terminal;
        Emulator = emulator;
        _terminal.Output += OnOutput;
        _terminal.Exited += (_, code) => Dispatcher.UIThread.Post(() => OnExited(code));
        Emulator.Response += (_, answer) => _terminal.Write(answer);
        Emulator.TitleChanged += (_, title) => TitleChanged?.Invoke(this, title);
    }

    /// <summary>Raised on the UI thread after new output was applied to the screen.</summary>
    public event EventHandler? ScreenChanged;

    /// <summary>Raised on the UI thread when the shell ends.</summary>
    public event EventHandler<int>? Exited;

    public event EventHandler<string>? TitleChanged;

    public TerminalEmulator Emulator { get; }

    public bool HasExited { get; private set; }

    /// <param name="commandLine">The shell to run; null for the default (PowerShell).</param>
    public static TerminalSession Start(string workingDirectory, int columns, int rows, string? commandLine = null)
    {
        var emulator = new TerminalEmulator(columns, rows);
        return new TerminalSession(PseudoTerminal.StartShell(commandLine, workingDirectory, columns, rows), emulator);
    }

    /// <summary>Sends typed keys to the shell.</summary>
    public void Write(string text)
    {
        if (!HasExited)
        {
            _terminal.Write(text);
        }
    }

    /// <summary>
    /// Sends pasted text. Line breaks become Enter presses, and when the shell asked for bracketed
    /// paste the text is marked as pasted so it is not run line by line.
    /// </summary>
    public void Paste(string text)
    {
        var normalized = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        Write(Emulator.BracketedPaste ? $"\x1b[200~{normalized}\x1b[201~" : normalized);
    }

    public void Resize(int columns, int rows)
    {
        if (columns == Emulator.Buffer.Columns && rows == Emulator.Buffer.Rows)
        {
            return;
        }

        Emulator.Resize(columns, rows);
        if (!HasExited)
        {
            _terminal.Resize(columns, rows);
        }
    }

    public void Dispose()
    {
        _terminal.Output -= OnOutput;
        _terminal.Dispose();
    }

    private void OnOutput(object? sender, string text)
    {
        lock (_pendingLock)
        {
            _pending.Append(text);
            if (_flushScheduled)
            {
                return;
            }

            _flushScheduled = true;
        }

        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Render);
    }

    private void Flush()
    {
        string text;
        lock (_pendingLock)
        {
            text = _pending.ToString();
            _pending.Clear();
            _flushScheduled = false;
        }

        Emulator.Feed(text);
        ScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnExited(int exitCode)
    {
        Flush();
        HasExited = true;
        Exited?.Invoke(this, exitCode);
    }
}
