using Coder.Core.Editing;
using Coder.Core.Syntax;
using Coder.Core.Text;

namespace Coder.Core.Tests;

public sealed class IndentationTests
{
    [Theory]
    [InlineData("class A\n{\n  void B()\n  {\n    x();\n  }\n}\n", true, 2)]
    [InlineData("class A\n{\n    void B()\n    {\n        x();\n    }\n}\n", true, 4)]
    [InlineData("a:\n        b: 1\n        c:\n                d: 2\n", true, 8)]
    [InlineData("func a() {\n\tif x {\n\t\ty()\n\t}\n}\n", false, 4)]
    public void Detects_spaces_and_tabs(string text, bool spaces, int size)
    {
        var detected = Indentation.Detect(new TextDocument(text));

        Assert.Equal((spaces, size), detected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("one line\nanother\n")]
    public void Says_nothing_without_indented_lines(string text) => Assert.Null(Indentation.Detect(new TextDocument(text)));

    /// <summary>The " * " of block comments lines up with the "/" above; it isn't a 1-space indent.</summary>
    [Fact]
    public void Ignores_block_comment_alignment()
    {
        var text = "/**\n * Doc.\n */\nclass A\n{\n    int x;\n    int y;\n}\n";

        Assert.Equal((true, 4), Indentation.Detect(new TextDocument(text)));
    }

    [Fact]
    public void Trims_trailing_whitespace_and_adds_a_final_newline_as_one_undo_step()
    {
        var document = new TextDocument("a  \n\tb\t\nc");
        var editor = new EditorState(document, Languages.PlainText);

        editor.ApplyEdits([.. Indentation.TrimTrailingWhitespace(document), .. Indentation.InsertFinalNewline(document)]);

        Assert.Equal("a\n\tb\nc\n", document.GetText());
        Assert.Empty(Indentation.TrimTrailingWhitespace(document));
        Assert.Empty(Indentation.InsertFinalNewline(document));
        Assert.Empty(Indentation.InsertFinalNewline(new TextDocument(string.Empty)));
    }
}
