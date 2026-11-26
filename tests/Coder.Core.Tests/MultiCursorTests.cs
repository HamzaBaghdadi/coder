using Coder.Core.Editing;
using Coder.Core.Syntax;
using Coder.Core.Text;

namespace Coder.Core.Tests;

public sealed class MultiCursorTests
{
    private static readonly LanguageDefinition CSharp = Languages.FromFileName("a.cs");

    [Fact]
    public void Typing_inserts_at_every_cursor_and_undoes_in_one_step()
    {
        var editor = Create("a\nb\nc");
        editor.SetSelections(Carets(1, 3, 5));

        editor.TypeText("x");
        editor.TypeText("y");

        Assert.Equal("axy\nbxy\ncxy", editor.Document.GetText());
        Assert.Equal([3, 7, 11], editor.Selections.All.Select(s => s.Active));

        editor.Undo();
        Assert.Equal("a\nb\nc", editor.Document.GetText());
        Assert.Equal(3, editor.Selections.Count);
    }

    [Fact]
    public void Backspace_at_adjacent_cursors_merges_them()
    {
        var editor = Create("abcd");
        editor.SetSelections(Carets(2, 3));

        editor.Backspace();
        Assert.Equal("ad", editor.Document.GetText());

        editor.Backspace();
        Assert.Equal("d", editor.Document.GetText());
        Assert.Equal(1, editor.Selections.Count);
    }

    [Fact]
    public void Enter_keeps_each_cursors_indentation()
    {
        var editor = Create("  a\n    b");
        editor.SetSelections(Carets(3, 9));

        editor.InsertNewLine();

        Assert.Equal("  a\n  \n    b\n    ", editor.Document.GetText());
    }

    [Fact]
    public void Add_next_occurrence_selects_word_then_each_match()
    {
        var editor = Create("foo bar foo baz foo");
        editor.SetCaret(1);

        editor.AddNextOccurrence();
        Assert.Equal("foo", editor.SelectedText);

        editor.AddNextOccurrence();
        editor.AddNextOccurrence();
        Assert.Equal([0, 8, 16], editor.Selections.All.Select(s => s.Start));

        editor.TypeText("q");
        Assert.Equal("q bar q baz q", editor.Document.GetText());
    }

    [Fact]
    public void Select_all_occurrences_of_the_word_uses_whole_words()
    {
        var editor = Create("item items item");
        editor.SetCaret(0);

        editor.SelectAllOccurrences();

        Assert.Equal([0, 11], editor.Selections.All.Select(s => s.Start));
    }

    [Fact]
    public void Cursors_on_the_same_line_comment_it_once()
    {
        var editor = Create("a();\nb();");
        editor.SetSelections(Carets(0, 2, 5));

        editor.ToggleLineComment();

        Assert.Equal("// a();\n// b();", editor.Document.GetText());
    }

    [Fact]
    public void Move_lines_moves_each_group_of_lines()
    {
        var editor = Create("1\n2\n3\n4\n5");
        editor.SetSelections(Carets(2, 6)); // lines "2" and "4"

        editor.MoveLines(up: true);

        Assert.Equal("2\n1\n4\n3\n5", editor.Document.GetText());
        Assert.Equal([0, 4], editor.Selections.All.Select(s => s.Active));
    }

    [Fact]
    public void Copy_and_paste_distribute_one_line_per_cursor()
    {
        var editor = Create("ab cd");
        editor.SetSelections(SelectionSet.Create([new Selection(0, 2), new Selection(3, 5)], new Selection(3, 5)));
        var copied = editor.GetCopyText();
        Assert.Equal("ab\ncd", copied);

        editor.SetSelections(Carets(0, 3));
        editor.Paste(copied);

        Assert.Equal("abab cdcd", editor.Document.GetText());
    }

    [Fact]
    public void Vertical_movement_keeps_each_cursors_column()
    {
        var editor = Create("abcd\nx\nefgh\nijkl");
        editor.SetSelections(Carets(3, 13)); // column 3 on lines 0 and 2

        editor.MoveCaret(CaretMovement.Down); // line 1 is short; line 3 keeps column 3
        editor.MoveCaret(CaretMovement.Down);

        Assert.Equal([new TextPosition(2, 3), new TextPosition(3, 4)], editor.Selections.All.Select(s => editor.Document.GetPosition(s.Active)));
    }

    [Fact]
    public void Escape_collapses_to_the_primary_cursor()
    {
        var editor = Create("a\nb");
        editor.SetCaret(0);
        editor.AddCaretVertically(above: false);

        Assert.Equal(2, editor.Selections.Count);
        Assert.True(editor.CollapseToPrimary());
        Assert.Equal(new TextPosition(1, 0), editor.CaretPosition);
        Assert.False(editor.CollapseToPrimary());
    }

    private static SelectionSet Carets(params int[] offsets) =>
        SelectionSet.Create(offsets.Select(Selection.Caret), Selection.Caret(offsets[^1]));

    private static EditorState Create(string text) => new(new TextDocument(text), CSharp);
}
