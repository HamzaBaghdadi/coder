using Coder.Core.Search;
using Coder.Core.Syntax;
using Coder.Core.Text;

namespace Coder.Core.Tests;

public sealed class SyntaxTests
{
    [Fact]
    public void Lexer_classifies_common_csharp_tokens()
    {
        var lexer = new LineLexer(Languages.FromFileName("x.cs"));
        var tokens = new List<Token>();
        const string line = "var name = Format(\"hi\", 42); // done";

        lexer.Tokenize(line, LexState.Normal, tokens);

        Assert.Contains(tokens, t => Text(line, t) == "var" && t.Kind == TokenKind.Keyword);
        Assert.Contains(tokens, t => Text(line, t) == "Format" && t.Kind == TokenKind.Function);
        Assert.Contains(tokens, t => Text(line, t) == "\"hi\"" && t.Kind == TokenKind.String);
        Assert.Contains(tokens, t => Text(line, t) == "42" && t.Kind == TokenKind.Number);
        Assert.Contains(tokens, t => Text(line, t) == "// done" && t.Kind == TokenKind.Comment);
    }

    [Fact]
    public void Block_comment_state_carries_across_lines_and_updates_after_edits()
    {
        var document = new TextDocument("a /* start\nmiddle\nend */ b");
        var highlighter = new SyntaxHighlighter(document, Languages.FromFileName("x.cs"));
        var tokens = new List<Token>();

        highlighter.GetTokens(1, tokens);
        Assert.Equal(TokenKind.Comment, Assert.Single(tokens).Kind);

        // Removing the opener must re-color the following lines.
        var caret = SelectionSet.Single(default);
        document.Edit(EditKind.Command, caret, edit =>
        {
            edit.Delete(2, 2);
            return caret;
        });
        highlighter.GetTokens(1, tokens);
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Comment);
    }

    [Theory]
    [InlineData("wbvm", "WorkbenchViewModel.cs")]
    [InlineData("edst", "EditorState.cs")]
    public void Fuzzy_matcher_finds_camel_case_abbreviations(string query, string candidate) =>
        Assert.NotNull(FuzzyMatcher.Score(query, candidate));

    [Fact]
    public void Fuzzy_matcher_prefers_word_start_matches() =>
        Assert.True(FuzzyMatcher.Score("es", "EditorState")! > FuzzyMatcher.Score("es", "Messages")!);

    private static string Text(string line, Token token) => line.Substring(token.Start, token.Length);
}
