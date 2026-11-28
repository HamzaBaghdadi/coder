using Coder.Core.Editing;
using Coder.Core.Text;

namespace Coder.Core.Tests;

public sealed class FoldingTests
{
    [Fact]
    public void A_fold_hides_the_lines_after_its_first_and_rows_skip_them()
    {
        var folds = new FoldingState();
        folds.Collapse(new ScopeRange(2, 5)); // hides 3, 4, 5

        Assert.Equal([false, false, false, true, true, true, false], Enumerable.Range(0, 7).Select(folds.IsHidden));
        Assert.Equal([0, 1, 2, 2, 2, 2, 3, 4], Enumerable.Range(0, 8).Select(folds.RowOf));
        Assert.Equal([0, 1, 2, 6, 7], Enumerable.Range(0, 5).Select(folds.LineAtRow));
        Assert.Equal(7, folds.VisibleLineCount(10));
    }

    [Fact]
    public void Nested_and_separate_folds_map_correctly()
    {
        var folds = new FoldingState();
        folds.Collapse(new ScopeRange(1, 8));
        folds.Collapse(new ScopeRange(3, 5)); // inside the first: changes nothing visible
        folds.Collapse(new ScopeRange(10, 12));

        Assert.Equal([0, 1, 9, 10, 13], Enumerable.Range(0, 5).Select(folds.LineAtRow));
        Assert.Equal(4, folds.RowOf(13));
        Assert.Equal(20 - 7 - 2, folds.VisibleLineCount(20));

        folds.Expand(1); // the inner fold shows again
        Assert.Equal([0, 1, 2, 3, 6], Enumerable.Range(0, 5).Select(folds.LineAtRow));
    }

    [Fact]
    public void Up_and_down_step_over_folds()
    {
        var folds = new FoldingState();
        folds.Collapse(new ScopeRange(2, 5));
        Assert.Equal(6, folds.Step(2, +1));
        Assert.Equal(2, folds.Step(6, -1));
        Assert.Equal(1, folds.Step(2, -1));
    }

    [Fact]
    public void Edits_move_folds_below_them_and_open_folds_they_touch()
    {
        var document = new TextDocument(string.Join("\n", Enumerable.Range(0, 12).Select(i => $"line {i}")));
        var folds = new FoldingState();
        document.Changed += (_, change) => folds.OnTextChanged(change);
        folds.Collapse(new ScopeRange(4, 7));
        folds.Collapse(new ScopeRange(9, 11));

        // Two lines added above: both folds move down.
        Insert(document, 0, "a\nb\n");
        Assert.Equal([new ScopeRange(6, 9), new ScopeRange(11, 13)], folds.Collapsed);

        // Typing on a fold's first line keeps it.
        Insert(document, document.GetLineEnd(6), "!");
        Assert.True(folds.IsCollapsed(6));

        // An edit inside the first fold's hidden lines opens it; the one below stays.
        Insert(document, document.GetLineStart(8), "x");
        Assert.Equal([new ScopeRange(11, 13)], folds.Collapsed);

        // A new line at a fold's first line opens it too.
        Insert(document, document.GetLineEnd(11), "\n");
        Assert.False(folds.HasFolds);
    }

    [Fact]
    public void Revealing_a_hidden_line_opens_the_folds_around_it()
    {
        var folds = new FoldingState();
        folds.Collapse(new ScopeRange(1, 9));
        folds.Collapse(new ScopeRange(3, 6));
        folds.Collapse(new ScopeRange(12, 14));
        Assert.True(folds.Reveal(4));
        Assert.Equal([new ScopeRange(12, 14)], folds.Collapsed);
        Assert.False(folds.Reveal(0));
    }

    [Fact]
    public void Fold_ranges_start_at_the_opening_bracket_and_end_before_the_closing_one()
    {
        var csharp = Coder.Core.Syntax.Languages.All.First(l => l.Id == "csharp");
        var document = new TextDocument("""
            class A
            {
                void M()
                {
                    var x = 1;
                    var y = 2;
                }
            }
            """.ReplaceLineEndings("\n"));

        var ranges = StickyScopes.FoldRanges(document, csharp, 4);

        Assert.Equal([new ScopeRange(1, 6), new ScopeRange(3, 5)], ranges); // the "{" lines, not the headers above them
    }

    [Fact]
    public void Fold_ranges_follow_indentation_where_there_are_no_braces()
    {
        var python = Coder.Core.Syntax.Languages.All.First(l => l.Id == "python");
        var document = new TextDocument("def f():\n    a = 1\n    if a:\n        b = 2\nprint(1)\n");
        Assert.Equal([new ScopeRange(0, 3), new ScopeRange(2, 3)], StickyScopes.FoldRanges(document, python, 4));
    }

    private static void Insert(TextDocument document, int offset, string text) =>
        document.Edit(EditKind.Typing, SelectionSet.Single(Selection.Caret(offset)), edit =>
        {
            edit.Replace(offset, 0, text);
            return SelectionSet.Single(Selection.Caret(offset + text.Length));
        });
}
