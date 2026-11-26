using Coder.Core.Terminal;

namespace Coder.Core.Tests;

public sealed class ShellIntegrationTests
{
    private const string Esc = "\x1b";
    private const string Bel = "\a";

    [Fact]
    public void Prompts_are_marked_and_finished_commands_get_their_exit_code()
    {
        var emulator = new TerminalEmulator(40, 10);
        var finished = new List<int?>();
        emulator.CommandFinished += (_, mark) => finished.Add(mark.ExitCode);

        emulator.Feed($"{Esc}]633;P;Cwd=C:\\work{Bel}{Esc}]633;A{Bel}PS C:\\work> {Esc}]633;B{Bel}");
        emulator.Feed("dir\r\nfile.txt\r\n");
        emulator.Feed($"{Esc}]633;D;0{Bel}{Esc}]633;A{Bel}PS C:\\work> {Esc}]633;B{Bel}");
        emulator.Feed("bad\r\nerror\r\n");
        emulator.Feed($"{Esc}]633;D;1{Bel}{Esc}]633;A{Bel}PS C:\\work> ");

        var marked = Enumerable.Range(0, emulator.Buffer.TotalLines).Select(emulator.Buffer.LineAt).Where(l => l.Mark is not null).ToList();
        Assert.Equal(3, marked.Count);
        Assert.Equal(["PS C:\\work> dir", "PS C:\\work> bad", "PS C:\\work>"], marked.Select(l => l.Text));
        Assert.True(marked[0].Mark!.Succeeded);
        Assert.True(marked[1].Mark!.Failed);
        Assert.Null(marked[2].Mark!.ExitCode); // still waiting
        Assert.Equal([0, 1], finished);
        Assert.Equal("C:\\work", emulator.CurrentDirectory);
        Assert.True(emulator.HasShellIntegration);
    }

    [Theory]
    [InlineData("7;file://desktop/C:/Users/me/src", "C:\\Users\\me\\src")]
    [InlineData("7;file://host/home/me/my%20src", "/home/me/my src")]
    [InlineData("9;9;\"D:\\Projects\"", "D:\\Projects")]
    public void The_current_folder_comes_from_any_of_the_usual_sequences(string osc, string folder)
    {
        var emulator = new TerminalEmulator(40, 5);
        emulator.Feed($"{Esc}]{osc}{Esc}\\");
        Assert.Equal(folder, emulator.CurrentDirectory);
    }
}
