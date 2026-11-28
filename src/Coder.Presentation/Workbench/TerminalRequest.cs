namespace Coder.Presentation.Workbench;

/// <summary>A terminal someone asked for (an extension's "open Claude"): the default shell, started in a folder with extra variables, then the command typed into it.</summary>
/// <param name="Name">The terminal's name in the list ("Claude Code").</param>
/// <param name="CommandLine">Typed into the shell once it starts (null: just the shell), so the shell stays when it ends.</param>
/// <param name="WorkingDirectory">Where it starts; null for the open folder.</param>
public sealed record TerminalRequest(string Name, string? CommandLine, string? WorkingDirectory, IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>An extension's side-bar view to show it in (<see cref="ExtensionSideBarView.Id"/>), instead of the terminal panel.</summary>
    public string? View { get; init; }

    /// <summary>Text shown on the terminal's screen before the shell's output (instructions, a notice).</summary>
    public string? Message { get; init; }

    /// <summary>
    /// A program to run in the terminal instead of the shell, with <see cref="Arguments"/> passed exactly
    /// (no shell quoting); <see cref="CommandLine"/> is then ignored. The terminal ends when the program does.
    /// </summary>
    public string? Program { get; init; }

    /// <summary>The arguments for <see cref="Program"/>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The command line that starts <see cref="Program"/>, or null when the request is for a shell.</summary>
    public string? ProgramCommandLine => Program is { } program ? Coder.Core.Terminal.CommandLines.Windows(program, Arguments) : null;
}
