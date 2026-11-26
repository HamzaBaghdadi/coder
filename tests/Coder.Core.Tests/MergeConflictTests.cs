using Coder.Core.Editing;
using Coder.Core.Git;
using Coder.Core.Syntax;
using Coder.Core.Text;

namespace Coder.Core.Tests;

public sealed class MergeConflictTests
{
    private const string TwoConflicts =
        "intro\n" +
        "<<<<<<< HEAD\n" +
        "ours 1\n" +
        "ours 2\n" +
        "=======\n" +
        "theirs\n" +
        ">>>>>>> feature\n" +
        "middle\n" +
        "<<<<<<< HEAD\n" +
        "mine\n" +
        "||||||| base\n" +
        "original\n" +
        "=======\n" +
        "yours\n" +
        ">>>>>>> feature\n" +
        "end\n";

    [Fact]
    public void Finds_conflict_blocks_with_and_without_a_common_ancestor()
    {
        var conflicts = MergeConflicts.Find(new TextDocument(TwoConflicts));

        Assert.Equal([new MergeConflict(1, -1, 4, 6), new MergeConflict(8, 10, 12, 14)], conflicts);
        Assert.Equal((2, 3), conflicts[0].Current);
        Assert.Equal((5, 5), conflicts[0].Incoming);
        Assert.Equal((9, 9), conflicts[1].Current);
        Assert.Equal((11, 11), conflicts[1].Ancestor);
    }

    [Fact]
    public void Ignores_text_that_only_looks_like_markers()
    {
        const string markdown = "Title\n=======\n\n<<<<<<<< eight\n>>>>>>> alone\n<<<<<<< HEAD\nunfinished\n";
        Assert.Empty(MergeConflicts.Find(new TextDocument(markdown)));
    }

    [Theory]
    [InlineData(ConflictResolution.Current, "intro\nours 1\nours 2\nmiddle\n")]
    [InlineData(ConflictResolution.Incoming, "intro\ntheirs\nmiddle\n")]
    [InlineData(ConflictResolution.Both, "intro\nours 1\nours 2\ntheirs\nmiddle\n")]
    public void Resolving_keeps_the_chosen_lines_and_drops_the_markers(ConflictResolution resolution, string expectedStart)
    {
        var editor = Editor(TwoConflicts);
        MergeConflicts.Resolve(editor, MergeConflicts.Find(editor.Document)[0], resolution);

        Assert.StartsWith(expectedStart, editor.Document.GetText(), StringComparison.Ordinal);
        Assert.Single(MergeConflicts.Find(editor.Document));
        editor.Document.Undo();
        Assert.Equal(TwoConflicts, editor.Document.GetText());
    }

    [Fact]
    public void Resolving_all_is_one_undo_step()
    {
        var editor = Editor(TwoConflicts);

        Assert.Equal(2, MergeConflicts.ResolveAll(editor, ConflictResolution.Incoming));
        Assert.Equal("intro\ntheirs\nmiddle\nyours\nend\n", editor.Document.GetText());
        editor.Document.Undo();
        Assert.Equal(TwoConflicts, editor.Document.GetText());
    }

    [Fact]
    public void A_conflict_at_the_end_of_the_file_adds_no_line_break()
    {
        var editor = Editor("a\n<<<<<<< HEAD\nb\n=======\nc\n>>>>>>> x");
        MergeConflicts.Resolve(editor, MergeConflicts.Find(editor.Document)[0], ConflictResolution.Both);
        Assert.Equal("a\nb\nc", editor.Document.GetText());
    }

    private static EditorState Editor(string text) => new(new TextDocument(text), Languages.PlainText);
}
