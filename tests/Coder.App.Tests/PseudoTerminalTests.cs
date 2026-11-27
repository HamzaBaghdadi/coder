using System.Collections.Concurrent;
using Coder.App.Terminal;
using Coder.Core.Terminal;

namespace Coder.App.Tests;

/// <summary>Runs a real shell through the pseudo-terminal (Windows only).</summary>
public sealed class PseudoTerminalTests
{
    [Fact]
    public void Shell_output_reaches_the_emulator_and_typed_input_reaches_the_shell()
    {
        if (!PseudoTerminal.IsSupported)
        {
            Assert.Skip("Needs Windows 10 1809 or later.");
            return;
        }

        var emulator = new TerminalEmulator(80, 20);
        var chunks = new ConcurrentQueue<string>();
        using IPseudoTerminal terminal = new WindowsPseudoTerminal("cmd.exe /Q /K prompt $G", Path.GetTempPath(), 80, 20);
        var received = 0;
        int? exitCode = null;
        var raw = new ConcurrentQueue<string>();
        terminal.Output += (_, text) =>
        {
            Interlocked.Increment(ref received);
            raw.Enqueue(text.Replace("\x1b", "ESC", StringComparison.Ordinal));
            chunks.Enqueue(text);
        };
        terminal.Exited += (_, code) => exitCode = code;
        emulator.Response += (_, answer) => terminal.Write(answer);

        terminal.Write("echo coder-terminal-ok\r");

        var screen = WaitForScreen(emulator, chunks, text => CountOf(text, "coder-terminal-ok") >= 2);
        Assert.True(CountOf(screen, "coder-terminal-ok") >= 2, $"chunks: {received}, exit: {exitCode?.ToString() ?? "running"}, raw: {string.Join(" | ", raw)}, screen:\n{screen}"); // the typed command and its output
    }

    /// <summary>
    /// Regression: killing a terminal closed the process handle while the exit-watching thread still
    /// used it, and that thread's exception ended the app (New Terminal and Kill Terminal both crashed).
    /// An unhandled exception on that thread would take down this test run too.
    /// </summary>
    [Fact]
    public void Killing_running_shells_is_clean_and_reports_no_exit()
    {
        if (!PseudoTerminal.IsSupported)
        {
            Assert.Skip("Needs Windows 10 1809 or later.");
            return;
        }

        var exits = 0;
        for (var i = 0; i < 4; i++)
        {
            IPseudoTerminal terminal = new WindowsPseudoTerminal("cmd.exe /Q /K", Path.GetTempPath(), 80, 20);
            terminal.Exited += (_, _) => Interlocked.Increment(ref exits);
            Thread.Sleep(i * 150); // kill at different moments of the shell's startup
            terminal.Dispose();
        }

        Thread.Sleep(2000); // give the exit threads time to wake up and finish
        Assert.Equal(0, exits);
    }

    private static string WaitForScreen(TerminalEmulator emulator, ConcurrentQueue<string> chunks, Func<string, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var screen = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            while (chunks.TryDequeue(out var chunk))
            {
                emulator.Feed(chunk);
            }

            screen = string.Join("\n", Enumerable.Range(0, emulator.Buffer.TotalLines).Select(i => emulator.Buffer.LineAt(i).Text));
            if (done(screen))
            {
                break;
            }

            Thread.Sleep(50);
        }

        return screen;
    }

    private static int CountOf(string text, string value) => text.Split(value).Length - 1;

    /// <summary>An extension's terminal gets its variables (Claude Code finds Coder through them), on top of Coder's own.</summary>
    [Fact]
    public void Extra_environment_variables_reach_the_shell()
    {
        if (!PseudoTerminal.IsSupported)
        {
            Assert.Skip("Needs Windows 10 1809 or later.");
            return;
        }

        var emulator = new TerminalEmulator(100, 20);
        var chunks = new ConcurrentQueue<string>();
        using IPseudoTerminal terminal = new WindowsPseudoTerminal("cmd.exe /Q /K prompt $G", Path.GetTempPath(), 100, 20,
            new Dictionary<string, string> { ["CODER_TEST_PORT"] = "40123" });
        terminal.Output += (_, text) => chunks.Enqueue(text);
        emulator.Response += (_, answer) => terminal.Write(answer);

        terminal.Write("echo port=%CODER_TEST_PORT% path=%PATH:~0,3%\r");

        var screen = WaitForScreen(emulator, chunks, text => text.Contains("port=40123 path=", StringComparison.Ordinal));
        Assert.Contains("port=40123 path=", screen, StringComparison.Ordinal);
        Assert.Matches("port=40123 path=[^%]", screen); // the output line: Coder's own variables (PATH) are still there
    }

    [Fact]
    public void The_environment_block_is_sorted_and_double_terminated()
    {
        if (!PseudoTerminal.IsSupported)
        {
            Assert.Skip("Needs Windows 10 1809 or later.");
            return;
        }

        var block = WindowsPseudoTerminal.EnvironmentBlock(new Dictionary<string, string> { ["ZZ_CODER_LAST"] = "1", ["path"] = "replaced" });
        var entries = block.TrimEnd('\0').Split('\0');

        Assert.EndsWith("\0\0", block, StringComparison.Ordinal);
        Assert.Equal(entries.OrderBy(e => e.Split('=')[0], StringComparer.OrdinalIgnoreCase), entries);
        Assert.Contains("ZZ_CODER_LAST=1", entries);
        Assert.Single(entries, e => e.StartsWith("path=", StringComparison.OrdinalIgnoreCase)); // replaced, case-insensitively
    }
}
