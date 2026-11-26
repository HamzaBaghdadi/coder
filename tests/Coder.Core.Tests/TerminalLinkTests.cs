using Coder.Core.Terminal;

namespace Coder.Core.Tests;

public sealed class TerminalLinkTests
{
    [Theory]
    [InlineData("see https://example.com/docs?a=1.", "https://example.com/docs?a=1")]
    [InlineData("(docs at https://example.com/x)", "https://example.com/x")]
    [InlineData("Now listening on: http://localhost:5000", "http://localhost:5000")]
    public void Finds_web_addresses_without_trailing_punctuation(string text, string url)
    {
        var link = Assert.Single(TerminalLinks.Find(text));
        Assert.Equal((TerminalLinkKind.Url, url), (link.Kind, link.Target));
        Assert.Equal(url, text[link.StartColumn..link.EndColumn]);
    }

    [Theory]
    [InlineData(@"src\Program.cs(12,5): error CS1002: ; expected", @"src\Program.cs", 12, 5)]   // MSBuild
    [InlineData("main.go:7:2: undefined: helper", "main.go", 7, 2)]                              // Go, gcc, tsc --pretty false
    [InlineData("  at Object.<anonymous> (/home/me/app/index.js:3:9)", "/home/me/app/index.js", 3, 9)] // Node stack trace
    [InlineData(@"Edited C:\Projects\demo\notes.md", @"C:\Projects\demo\notes.md", null, null)]
    [InlineData("./scripts/build.sh:14", "./scripts/build.sh", 14, null)]
    public void Finds_file_paths_with_their_line_and_column(string text, string path, int? line, int? column)
    {
        var link = Assert.Single(TerminalLinks.Find(text), l => l.Kind == TerminalLinkKind.File);
        Assert.Equal((path, line, column), (link.Target, link.Line, link.Column));
    }

    [Fact]
    public void Plain_words_and_version_numbers_are_not_links()
    {
        Assert.Empty(TerminalLinks.Find("Build succeeded. 0 Warning(s) in 1.5 seconds"));
        Assert.Empty(TerminalLinks.Find("D:\\Projects\\Personal\\Coder [main *]>"));
    }

    [Fact]
    public void Columns_are_cells_in_a_terminal_line()
    {
        var emulator = new TerminalEmulator(60, 2);
        emulator.Feed("日本 https://example.com");

        var link = Assert.Single(TerminalLinks.Find(emulator.Buffer.ScreenLine(0)));

        Assert.Equal((5, 5 + "https://example.com".Length), (link.StartColumn, link.EndColumn)); // after two wide characters and a space
    }
}
