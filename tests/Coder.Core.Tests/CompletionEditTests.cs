using Coder.Core.Editing;
using Coder.Core.Syntax;
using Coder.Core.Text;

namespace Coder.Core.Tests;

public sealed class CompletionEditTests
{
    [Fact]
    public void Replaces_the_typed_prefix_and_selects_the_placeholder()
    {
        var editor = new EditorState(new TextDocument("fmt.Pri"), Languages.PlainText);
        editor.SetCaret(7);

        editor.InsertCompletion(4, "Println(a)", selectionStart: 8, selectionLength: 1, []);

        Assert.Equal("fmt.Println(a)", editor.Document.GetText());
        Assert.Equal("a", editor.SelectedText);
    }

    [Fact]
    public void Applies_at_every_cursor_with_the_same_prefix_plus_an_import_once_as_one_undo_step()
    {
        const string Text = "package main\n\nfunc a() { Pri }\nfunc b() { Pri }\n";
        var editor = new EditorState(new TextDocument(Text), Languages.PlainText);
        var first = Text.IndexOf("Pri", StringComparison.Ordinal) + 3;
        var second = Text.LastIndexOf("Pri", StringComparison.Ordinal) + 3;
        editor.SetSelections(SelectionSet.Create([Selection.Caret(first), Selection.Caret(second)], Selection.Caret(second)));

        editor.InsertCompletion(second - 3, "Println()", selectionStart: 8, selectionLength: 0, [(14, 14, "import \"fmt\"\n")]);

        Assert.Equal("package main\n\nimport \"fmt\"\nfunc a() { Println() }\nfunc b() { Println() }\n", editor.Document.GetText());
        var text = editor.Document.GetText();
        Assert.Equal(
            [text.IndexOf("Println()", StringComparison.Ordinal) + 8, text.LastIndexOf("Println()", StringComparison.Ordinal) + 8],
            editor.Selections.All.Select(s => s.Active));

        editor.Undo();
        Assert.Equal(Text, editor.Document.GetText());
    }

    [Fact]
    public void A_cursor_without_the_prefix_gets_the_text_inserted()
    {
        var editor = new EditorState(new TextDocument("ab\nxy"), Languages.PlainText);
        editor.SetSelections(SelectionSet.Create([Selection.Caret(2), Selection.Caret(5)], Selection.Caret(2)));

        editor.InsertCompletion(0, "abc", selectionStart: 3, selectionLength: 0, []);

        Assert.Equal("abc\nxyabc", editor.Document.GetText());
    }
}
