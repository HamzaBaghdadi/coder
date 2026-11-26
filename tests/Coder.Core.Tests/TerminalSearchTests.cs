using Coder.Core.Terminal;

namespace Coder.Core.Tests;

public sealed class TerminalSearchTests
{
    [Fact]
    public void Finds_matches_in_history_and_on_screen_ignoring_case()
    {
        var emulator = new TerminalEmulator(40, 3);
        emulator.Feed("error one\r\nok\r\nERROR two, error three\r\nlast\r\n");

        var matches = TerminalSearch.FindAll(emulator.Buffer, "error", matchCase: false);

        Assert.Equal([new TerminalMatch(0, 0, 5), new TerminalMatch(2, 0, 5), new TerminalMatch(2, 11, 16)], matches);
        Assert.True(emulator.Buffer.ScrollbackCount > 0, "the first match is in the history");
        Assert.Equal(2, TerminalSearch.FindAll(emulator.Buffer, "error", matchCase: true).Count);
        Assert.Empty(TerminalSearch.FindAll(emulator.Buffer, string.Empty, matchCase: false));
    }

    [Fact]
    public void Columns_count_wide_characters_as_two_cells()
    {
        var emulator = new TerminalEmulator(40, 3);
        emulator.Feed("日本 ok");

        var match = Assert.Single(TerminalSearch.FindAll(emulator.Buffer, "ok", matchCase: false));

        Assert.Equal(new TerminalMatch(0, 5, 7), match); // 日 and 本 take two cells each, then a space
        Assert.Equal(new TerminalMatch(0, 2, 4), Assert.Single(TerminalSearch.FindAll(emulator.Buffer, "本", matchCase: false)));
    }

    [Fact]
    public void Detects_the_shells_that_are_installed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Shell detection is for Windows for now.");
            return;
        }

        var profiles = ShellProfiles.Detect();

        Assert.Contains(profiles, p => p.Id == "cmd" && p.CommandLine.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(profiles, p => p.Id == "powershell");
        Assert.Contains("PowerShell", profiles[0].Name, StringComparison.Ordinal); // PowerShell (7, or Windows') is the default
        Assert.Equal("cmd", ShellProfiles.Resolve(profiles, "cmd")!.Id);
        Assert.Same(profiles[0], ShellProfiles.Resolve(profiles, "not-installed"));

        // Every command starts with a program that exists (quoted when its path has spaces).
        foreach (var profile in profiles)
        {
            var program = profile.CommandLine.StartsWith('"')
                ? profile.CommandLine[1..profile.CommandLine.IndexOf('"', 1)]
                : profile.CommandLine.Split(' ')[0];
            Assert.True(File.Exists(program), $"{profile.Name}: {program}");
        }
    }
}
