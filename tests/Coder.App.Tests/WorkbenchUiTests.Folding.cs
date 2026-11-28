using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Editor;
using Coder.App.Workbench;
using Coder.Core.Editing;
using Coder.Presentation.Commands;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// Folding: chevrons show while the mouse is over the gutter; clicking one folds its block (the lines go,
    /// the rows close up, a "⋯" follows the first line); Down steps over it; clicking the "⋯" unfolds it; Fold,
    /// Unfold, Fold All and Unfold All work on the caret's blocks; a caret sent inside a fold opens it; folds
    /// move with edits above them and stay with their tab.
    /// </summary>
    [AvaloniaFact]
    public async Task Code_folds_from_the_gutter_and_by_command()
    {
        var folder = Directory.CreateTempSubdirectory("coder-folding-").FullName;
        try
        {
            var (window, workbench) = CreateWindow();
            window.Show();
            var path = Path.Combine(folder, "Invoice.cs");
            await File.WriteAllTextAsync(path, StickyCSharp, TestContext.Current.CancellationToken);
            await workbench.OpenFileAsync(path);
            Render(window);
            var view = window.GetVisualDescendants().OfType<EditorView>().Single();
            var tab = workbench.Editors.ActiveEditor!;
            var lineHeight = view.ScrollSize.Height / 3;
            Point At(Point inView) => view.TranslatePoint(inView, window)!.Value;

            // The blocks: namespace, class, method, foreach, if (from each "{" line to the line before its "}").
            Assert.Equal([new ScopeRange(3, 34), new ScopeRange(5, 33), new ScopeRange(9, 32), new ScopeRange(12, 29), new ScopeRange(14, 28)], tab.FoldRanges());

            // Hovering the gutter shows the chevrons.
            window.MouseMove(At(view.FoldChevronCenter(9)));
            Render(window);
            Assert.True(view.IsGutterHovered);
            Assert.True(view.IsOverFoldControl(view.FoldChevronCenter(9))); // a hand pointer over a chevron
            Assert.False(view.IsOverFoldControl(view.FoldChevronCenter(10))); // not where nothing folds
            Save(window, "editor-folding-hover");

            // Clicking the method's chevron folds it: its closing "}" (line 33) moves up to the next row.
            window.MouseDown(At(view.FoldChevronCenter(9)), MouseButton.Left);
            window.MouseUp(At(view.FoldChevronCenter(9)), MouseButton.Left);
            Render(window);
            Assert.True(tab.Folding.IsCollapsed(9));
            Assert.Equal(33, view.LineShownAt(10 * lineHeight + 1));
            Save(window, "editor-folding-folded");

            // Down steps over the folded block.
            tab.Editor.SetCaret(tab.Document.GetLineStart(8));
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Assert.Equal(33, tab.Editor.CaretPosition.Line);
            Assert.True(tab.Folding.IsCollapsed(9)); // stepping over doesn't open it
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            Assert.Equal(9, tab.Editor.CaretPosition.Line);

            // Clicking the "⋯" unfolds it.
            Render(window);
            window.MouseDown(At(view.FoldedMarkerCenter(9)), MouseButton.Left);
            window.MouseUp(At(view.FoldedMarkerCenter(9)), MouseButton.Left);
            Assert.False(tab.Folding.HasFolds);

            // Fold folds the innermost block around the caret, moving the caret to its first line; Unfold opens it.
            tab.Editor.SetCaret(tab.Document.GetLineStart(16) + 4);
            await workbench.Commands.ExecuteAsync(CommandIds.Fold);
            Assert.Equal([new ScopeRange(14, 28)], tab.Folding.Collapsed);
            Assert.Equal(14, tab.Editor.CaretPosition.Line);
            await workbench.Commands.ExecuteAsync(CommandIds.Unfold);
            Assert.False(tab.Folding.HasFolds);

            // Fold All, then Unfold All.
            await workbench.Commands.ExecuteAsync(CommandIds.FoldAll);
            Assert.Equal(5, tab.Folding.Collapsed.Count);
            Assert.Equal(3, tab.Editor.CaretPosition.Line); // out of the outermost block
            Render(window);
            Assert.Equal(35, view.LineShownAt(4 * lineHeight + 1)); // using, blank, namespace, "{" ... then the last "}"
            await workbench.Commands.ExecuteAsync(CommandIds.UnfoldAll);
            Assert.False(tab.Folding.HasFolds);

            // A caret sent inside a fold (a search result, Go to Definition) opens it.
            tab.ToggleFold(new ScopeRange(12, 29));
            tab.Editor.SetCaret(tab.Document.GetLineStart(20));
            Assert.False(tab.Folding.HasFolds);

            // A fold moves with lines added above it, and stays with its tab.
            tab.ToggleFold(new ScopeRange(9, 32));
            tab.Editor.SetCaret(0);
            tab.Editor.TypeText("// header\n");
            Assert.Equal([new ScopeRange(10, 33)], tab.Folding.Collapsed);
            var other = Path.Combine(folder, "Other.cs");
            await File.WriteAllTextAsync(other, "class B { }\n", TestContext.Current.CancellationToken);
            await workbench.OpenFileAsync(other);
            Render(window);
            await workbench.OpenFileAsync(path);
            Render(window);
            Assert.True(workbench.Editors.ActiveEditor!.Folding.IsCollapsed(10));
            Assert.Equal(34, view.LineShownAt(11 * lineHeight + 1));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
