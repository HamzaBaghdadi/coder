namespace Coder.App.Terminal;

/// <summary>
/// A shell running in a pseudo-terminal: it believes it talks to a real terminal, so it prints colors,
/// prompts and full-screen programs exactly as it would in one.
/// </summary>
public interface IPseudoTerminal : IDisposable
{
    /// <summary>Raised on a background thread with each chunk of decoded output.</summary>
    event EventHandler<string>? Output;

    /// <summary>Raised (on a background thread) when the shell process ends.</summary>
    event EventHandler<int>? Exited;

    /// <summary>Sends keystrokes or pasted text to the shell.</summary>
    void Write(string text);

    void Resize(int columns, int rows);
}

/// <summary>Starts the user's shell in the right kind of pseudo-terminal for this operating system.</summary>
public static class PseudoTerminal
{
    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows10.0.17763")]
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    /// <param name="commandLine">The shell to run (see <see cref="Coder.Core.Terminal.ShellProfiles"/>); null for the default shell.</param>
    /// <exception cref="PlatformNotSupportedException">On systems without a supported pseudo-terminal yet.</exception>
    /// <param name="environment">Extra variables for the shell (an extension's terminal); null for none.</param>
    public static IPseudoTerminal StartShell(string? commandLine, string workingDirectory, int columns, int rows, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            commandLine ??= Coder.Core.Terminal.ShellProfiles.Resolve(Coder.Core.Terminal.ShellProfiles.Detect(), null)?.CommandLine ?? "powershell.exe -NoLogo";
            return new WindowsPseudoTerminal(commandLine, workingDirectory, columns, rows, environment);
        }

        throw new PlatformNotSupportedException("The integrated terminal currently needs Windows 10 (1809) or later.");
    }
}
