using Coder.Core.Editing;
using Coder.Core.Syntax;
using Coder.Core.Text;

namespace Coder.Core.Tests;

public sealed class EditorStateTests
{
    private static readonly LanguageDefinition CSharp = Languages.FromFileName("a.cs");

    [Fact]
    public void Typing_merges_into_one_undo_step_per_word()
    {
        var editor = Create(string.Empty);

        editor.TypeText("h");
        editor.TypeText("i");
        editor.TypeText(" ");
        editor.TypeText("x");
        editor.Undo();

        Assert.Equal("hi ", editor.Document.GetText());
        editor.Undo();
        Assert.Equal(string.Empty, editor.Document.GetText());
        Assert.False(editor.Document.IsModified);
    }

    [Fact]
    public void Brackets_auto_close_and_step_over()
    {
        var editor = Create(string.Empty);

        editor.TypeText("(");
        editor.TypeText("a");
        editor.TypeText(")");

        Assert.Equal("(a)", editor.Document.GetText());
        Assert.Equal(3, editor.Selection.Active);
    }

    [Fact]
    public void Backspace_inside_empty_pair_removes_both()
    {
        var editor = Create(string.Empty);
        editor.TypeText("[");

        editor.Backspace();

        Assert.Equal(string.Empty, editor.Document.GetText());
    }

    [Fact]
    public void Enter_between_braces_opens_an_indented_block()
    {
        var editor = Create("    if (x) {}");
        editor.SetCaret(12);

        editor.InsertNewLine();

        Assert.Equal("    if (x) {\n        \n    }", editor.Document.GetText());
        Assert.Equal(new TextPosition(1, 8), editor.CaretPosition);
    }

    [Fact]
    public void Vertical_movement_keeps_preferred_column()
    {
        var editor = Create("long line here\nab\nanother long line");
        editor.SetCaret(10);

        editor.MoveCaret(CaretMovement.Down);
        Assert.Equal(new TextPosition(1, 2), editor.CaretPosition);

        editor.MoveCaret(CaretMovement.Down);
        Assert.Equal(new TextPosition(2, 10), editor.CaretPosition);
    }

    [Fact]
    public void Toggle_comment_round_trips()
    {
        var editor = Create("  a();\n  b();");
        editor.SelectAll();

        editor.ToggleLineComment();
        Assert.Equal("  // a();\n  // b();", editor.Document.GetText());

        editor.ToggleLineComment();
        Assert.Equal("  a();\n  b();", editor.Document.GetText());
    }

    [Fact]
    public void Move_lines_down_carries_selection()
    {
        var editor = Create("one\ntwo\nthree");
        editor.SetCaret(1);

        editor.MoveLines(up: false);

        Assert.Equal("two\none\nthree", editor.Document.GetText());
        Assert.Equal(new TextPosition(1, 1), editor.CaretPosition);
    }

    [Fact]
    public void Line_endings_are_normalized_and_restored()
    {
        var document = new TextDocument("a\r\nb\r\n");

        Assert.Equal(LineEnding.CRLF, document.LineEnding);
        Assert.Equal("a\nb\n", document.GetText());
        Assert.Equal("a\r\nb\r\n", document.GetTextForSave());
    }

    [Fact]
    public void Smart_home_toggles_between_indent_and_column_zero()
    {
        var editor = Create("    code");
        editor.SetCaret(8);

        editor.MoveCaret(CaretMovement.LineStart);
        Assert.Equal(4, editor.Selection.Active);

        editor.MoveCaret(CaretMovement.LineStart);
        Assert.Equal(0, editor.Selection.Active);
    }

    private static EditorState Create(string text) => new(new TextDocument(text), CSharp);
}
