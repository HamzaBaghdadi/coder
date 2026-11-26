using Coder.Core.Lsp;

namespace Coder.Core.Tests;

public sealed class SnippetTests
{
    [Theory]
    [InlineData("Println(${1:a ...any})", "Println(a ...any)", 8, 8)]          // first placeholder selected
    [InlineData("for ${1:i} := 0; $1 < ${2:n}; $1++ {\n\t$0\n}", "for i := 0; i < n; i++ {\n\t\n}", 4, 1)] // mirrors copy the placeholder
    [InlineData("len($0)", "len()", 4, 0)]                                      // final position
    [InlineData("plain", "plain", 5, 0)]                                        // no tab stops: the end
    [InlineData("${2:b} ${1:a}", "b a", 2, 1)]                                  // lowest number wins, not first
    [InlineData("${1|one,two|}", "one", 0, 3)]                                  // choice: the first option
    [InlineData("${1:outer ${2:inner}}", "outer inner", 0, 11)]                 // nested placeholders
    [InlineData("cost: \\$5 \\}", "cost: $5 }", 10, 0)]                        // escapes
    [InlineData("$TM_FILENAME${VAR:default}x", "defaultx", 8, 0)]              // variables
    [InlineData("a $ b", "a $ b", 5, 0)]                                        // a lone dollar is text
    public void Expands_to_text_and_selection(string snippet, string text, int selectionStart, int selectionLength)
    {
        var expansion = Snippet.Expand(snippet);

        Assert.Equal(text, expansion.Text);
        Assert.Equal(selectionStart, expansion.SelectionStart);
        Assert.Equal(selectionLength, expansion.SelectionLength);
    }
}
