using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Coder.App.Editor;
using Coder.Core.Text;
using Coder.Presentation.Editors;

namespace Coder.App.Tests;

/// <summary>
/// Caret placement on text that is not one fixed-width cell per character: wide CJK characters,
/// emoji (surrogate pairs) and right-to-left Arabic.
/// </summary>
public sealed class InternationalTextTests
{
    [AvaloniaFact]
    public void Caret_after_wide_characters_sits_after_their_glyphs_not_on_a_fixed_grid()
    {
        var (view, tab) = CreateView("ab中文cd");
        var narrowEnd = CaretX(view, tab, 2);
        var afterWide = CaretX(view, tab, 4);

        // Two CJK glyphs are clearly wider than two monospace cells.
        var cell = CaretX(view, tab, 1) - CaretX(view, tab, 0);
        Assert.True(afterWide - narrowEnd > 2.5 * cell, $"CJK width {afterWide - narrowEnd:F1}, cell {cell:F1}");
    }

    [AvaloniaFact]
    public void Clicking_a_caret_position_returns_the_same_column_for_cjk_and_emoji()
    {
        const string line = "x 漢字 😀 y";
        var (view, tab) = CreateView(line);

        for (var column = 0; column <= line.Length; column++)
        {
            if (column > 0 && char.IsHighSurrogate(line[column - 1]))
            {
                continue; // between the two halves of an emoji is not a caret position
            }

            var x = CaretX(view, tab, column);
            Assert.Equal(column, view.ColumnAtX(0, x));
        }
    }

    [AvaloniaFact]
    public void Arabic_runs_right_to_left_inside_a_left_to_right_line()
    {
        const string line = "s = \"مرحبا\";";
        var (view, tab) = CreateView(line);
        var first = line.IndexOf('م', StringComparison.Ordinal);

        // Moving forward through the Arabic word moves the caret to the left on screen.
        for (var column = first + 1; column < first + 4; column++)
        {
            Assert.True(CaretX(view, tab, column + 1) < CaretX(view, tab, column), $"column {column}");
        }

        // The Latin text before it still runs left to right.
        Assert.True(CaretX(view, tab, 1) > CaretX(view, tab, 0));
    }

    /// <summary>
    /// Regression: once semi-bold UI text had been drawn, Avalonia's automatic fallback resolved Arabic
    /// to the coding font, which has no Arabic glyphs, so the editor showed empty boxes.
    /// </summary>
    [AvaloniaFact]
    public void Every_character_has_a_glyph_even_after_semibold_ui_text_was_drawn()
    {
        // Arabic is resolved once before the semi-bold text appears, as in the app.
        _ = new TextLayout("مرحبا", new Typeface(Theme.Typography.Code), 14, Brushes.White);
        var window = new Window { Width = 400, Height = 200, Content = new TextBlock { Text = "Coder", FontWeight = FontWeight.SemiBold } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        const string line = "const s = \"مرحبا بالعالم 你好 こんにちは 👋\";";
        var document = new TextDocument(line);
        var highlighter = new Core.Syntax.SyntaxHighlighter(document, Core.Syntax.Languages.FromFileName("a.ts"));
        var metrics = new EditorMetrics(new Typeface(Theme.Typography.Code), 14, 1.5);
        var layout = new LineLayoutCache().Get(0, document, highlighter, metrics, 4, 0, 200, []).Layout;

        // Every visible character must be drawn by a font that really contains it.
        var missing = layout.TextLines[0].TextRuns.OfType<ShapedTextRun>()
            .SelectMany(run => run.Text.ToString().EnumerateRunes()
                .Where(rune => !Rune.IsWhiteSpace(rune) && Rune.GetUnicodeCategory(rune) != System.Globalization.UnicodeCategory.Format)
                .Where(rune => !run.ShapedBuffer.GlyphTypeface.CharacterToGlyphMap.ContainsGlyph(rune.Value))
                .Select(rune => $"'{rune}' in {run.ShapedBuffer.GlyphTypeface.FamilyName}"))
            .ToList();
        Assert.Empty(missing);
    }

    private static double CaretX(EditorView view, EditorTabViewModel tab, int column)
    {
        tab.Editor.SetCaret(column);
        return view.CaretRectangle.X;
    }

    private static (EditorView View, EditorTabViewModel Tab) CreateView(string text)
    {
        var tab = new EditorTabViewModel("sample.ts", text, new System.Text.UTF8Encoding(false));
        var view = new EditorView();
        view.Measure(new Size(800, 400));
        view.Arrange(new Rect(0, 0, 800, 400));
        view.Attach(tab.Editor, tab.Highlighter, default);
        return (view, tab);
    }
}
