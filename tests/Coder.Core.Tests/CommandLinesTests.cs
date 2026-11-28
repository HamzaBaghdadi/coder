using Coder.Core.Terminal;

namespace Coder.Core.Tests;

public sealed class CommandLinesTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\a b\", "\"C:\\a b\\\\\"")] // a trailing backslash is doubled before the closing quote
    [InlineData(@"C:\path\file", @"C:\path\file")] // backslashes not before a quote stay as they are
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")] // a backslash before a quote is doubled, and the quote escaped
    public void Arguments_are_quoted_only_when_needed(string argument, string expected) =>
        Assert.Equal(expected, CommandLines.QuoteWindows(argument));

    [Fact]
    public void A_program_path_with_spaces_is_quoted()
    {
        Assert.Equal("\"C:\\Program Files\\tool.exe\" -x \"y z\"", CommandLines.Windows(@"C:\Program Files\tool.exe", ["-x", "y z"]));
        Assert.Equal("tool", CommandLines.Windows("tool", []));
    }

    /// <summary>What Windows itself parses back is exactly what was passed.</summary>
    [Fact]
    public void Windows_parses_the_arguments_back_unchanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string[] arguments = ["simple", "two words", "", "say \"hi\"", @"C:\a b\", @"tail\\", "a\\\"b", "tab\there", "irm https://claude.ai/install.ps1 | iex; & \"$env:USERPROFILE\\.local\\bin\\claude.exe\""];
        Assert.Equal(arguments, Parse(CommandLines.Windows("tool.exe", arguments)).Skip(1));
    }

    private static string[] Parse(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return [.. Enumerable.Range(0, count).Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)];
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
