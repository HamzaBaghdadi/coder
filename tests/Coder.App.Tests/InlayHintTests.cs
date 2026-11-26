using Avalonia;
using Avalonia.Headless.XUnit;
using Coder.App.Editor;
using Coder.Core.Lsp;
using Coder.Core.Text;
using Coder.Presentation.Editors;

namespace Coder.App.Tests;

/// <summary>Inlay hints take space in a line but not in the document: carets, clicks and selections skip them.</summary>
public sealed class InlayHintTests
{
    // "add(2, 3)" with parameter hints before both arguments and a type hint at the end.
    private const string Line = "add(2, 3)";

    private static readonly InlayHint[] Hints =
    [
        new(new TextPosition(0, 4), "a:", InlayHintKind.Parameter, PaddingLeft: false, PaddingRight: true),
        new(new TextPosition(0, 7), "b:", InlayHintKind.Parameter, PaddingLeft: false, PaddingRight: true),
        new(new TextPosition(0, 9), "int", InlayHintKind.Type, PaddingLeft: true, PaddingRight: false),
    ];

    [AvaloniaFact]
    public void Hints_push_text_right_and_the_caret_skips_them()
    {
        var (view, tab, charWidth) = CreateView();
        double CaretX(int column)
        {
            tab.Editor.SetCaret(column);
            return view.CaretRectangle.X;
        }

        var start = CaretX(0);
        Assert.Equal(start + 3 * charWidth, CaretX(3), 1);                // "add" is untouched
        Assert.Equal(start + (4 + 3) * charWidth, CaretX(4), 1);          // after "a: ", next to the argument
        Assert.Equal(start + (7 + 6) * charWidth, CaretX(7), 1);          // after "a: " and "b: "
        Assert.Equal(start + (9 + 6) * charWidth, CaretX(9), 1);          // the line end, before " int"

        // Moving the caret goes character by character; hints are never stepped into.
        tab.Editor.SetCaret(3);
        tab.Editor.MoveCaret(Coder.Core.Editing.CaretMovement.Right);
        Assert.Equal(4, tab.Editor.Selection.Active);
    }

    [AvaloniaFact]
    public void Clicking_a_hint_lands_at_its_position()
    {
        var (view, tab, charWidth) = CreateView();
        tab.Editor.SetCaret(0);
        var left = view.CaretRectangle.X;

        Assert.Equal(4, view.ColumnAtX(0, left + 5 * charWidth));          // on "a:" → before the argument it names
        Assert.Equal(5, view.ColumnAtX(0, left + (5 + 3) * charWidth + 1)); // just after "2"
        Assert.Equal(9, view.ColumnAtX(0, left + 40 * charWidth));          // past the end
    }

    [AvaloniaFact]
    public void Edits_keep_other_lines_hints_until_fresh_ones_arrive()
    {
        var tab = new EditorTabViewModel("sample.go", "a\nb\nc(1)\n", new System.Text.UTF8Encoding(false));
        tab.SetInlayHints([new InlayHint(new TextPosition(2, 2), "x:", InlayHintKind.Parameter, false, true)]);

        tab.Editor.SetCaret(0);
        tab.Editor.TypeText("new line\n");
        Assert.Single(tab.InlayHintsOn(3)); // moved down with its line
        Assert.Empty(tab.InlayHintsOn(2));

        tab.Editor.SetCaret(tab.Document.GetLineStart(3));
        tab.Editor.TypeText("z");
        Assert.Empty(tab.InlayHintsOn(3)); // its line was edited: gone until the server answers again
    }

    private static (EditorView View, EditorTabViewModel Tab, double CharWidth) CreateView()
    {
        var tab = new EditorTabViewModel("sample.go", Line, new System.Text.UTF8Encoding(false));
        tab.SetInlayHints(Hints);
        var view = new EditorView();
        view.Measure(new Size(800, 400));
        view.Arrange(new Rect(0, 0, 800, 400));
        view.Attach(tab.Editor, tab.Highlighter, default);
        view.SetInlayHintsSource(tab.InlayHintsOn);

        // A plain line of the same text measures the coding font's advance.
        tab.Editor.SetCaret(1);
        var one = view.CaretRectangle.X;
        tab.Editor.SetCaret(0);
        return (view, tab, one - view.CaretRectangle.X);
    }
}
