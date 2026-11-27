using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Editor;
using Coder.App.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    private const string StickyCSharp = """
        using System;

        namespace Demo.Billing
        {
            public sealed class InvoiceCalculator
            {
                private readonly decimal _taxRate;

                public decimal Total(IReadOnlyList<Line> lines)
                {
                    var total = 0m;
                    foreach (var line in lines)
                    {
                        if (line.Quantity > 0)
                        {
                            var price = line.UnitPrice * line.Quantity;
                            total += price;
                            total += price * _taxRate;
                            Console.WriteLine($"{line.Name}: {price}");
                            Console.WriteLine("subtotal {0}", total);
                            Console.WriteLine("tax {0}", _taxRate);
                            Console.WriteLine("lines {0}", lines.Count);
                            Console.WriteLine("done {0}", line.Name);
                            Console.WriteLine("next");
                            Console.WriteLine("more");
                            Console.WriteLine("and more");
                            Console.WriteLine("still more");
                            Console.WriteLine("almost");
                            Console.WriteLine("there");
                        }
                    }

                    return total;
                }
            }
        }
        """;

    /// <summary>
    /// Sticky scroll: the headers of the blocks around the top line stay pinned, stacked by nesting, in C#,
    /// TypeScript, JSON and HTML; clicking one goes to it; the setting turns it off.
    /// </summary>
    [AvaloniaFact]
    public async Task Sticky_scroll_pins_the_enclosing_block_headers()
    {
        var folder = Directory.CreateTempSubdirectory("coder-sticky-").FullName;
        try
        {
            var (window, workbench) = CreateWindow();
            window.Show();
            Assert.True(workbench.StickyScroll.IsEnabled); // on by default, like VS Code

            var view = await OpenAt(folder, "Invoice.cs", StickyCSharp, 17);
            var lineHeight = view.ScrollSize.Height / 3;
            // namespace, class, method, foreach (the "{" on its own line belongs to the line above), if.
            Assert.Equal([2, 4, 8, 11, 13], view.StickyLines);
            Save(window, "editor-sticky");

            // Clicking the method's header goes to it: the caret lands on it, just below the pinned class.
            Point At(double x, double y) => view.TranslatePoint(new Point(x, y), window)!.Value;
            var row = At(200, lineHeight * 2.5);
            window.MouseDown(row, MouseButton.Left);
            window.MouseUp(row, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(8, workbench.Editors.ActiveEditor!.Editor.CaretPosition.Line);
            Assert.Equal((8 - 2) * lineHeight, view.Offset.Y, 1);
            Render(window);
            Assert.Equal([2, 4], view.StickyLines);

            // TypeScript (K&R braces), JSON and HTML (indentation).
            var ts = string.Join('\n', ["export class Store {", "  load(id: string) {", "    if (id) {", .. Enumerable.Repeat("      fetch(id);", 30), "    }", "  }", "}"]);
            view = await OpenAt(folder, "store.ts", ts, 6);
            Assert.Equal([0, 1, 2], view.StickyLines);

            var json = string.Join('\n', ["{", "  \"items\": [", "    {", .. Enumerable.Range(0, 30).Select(i => $"      \"k{i}\": {i},"), "      \"end\": 0", "    }", "  ]", "}"]);
            view = await OpenAt(folder, "data.json", json, 10);
            Assert.Equal([0, 1, 2], view.StickyLines);

            var html = string.Join('\n', ["<html>", "  <body>", "    <main>", .. Enumerable.Repeat("      <p>text</p>", 30), "    </main>", "  </body>", "</html>"]);
            view = await OpenAt(folder, "index.html", html, 8);
            Assert.Equal([0, 1, 2], view.StickyLines);

            // Off in Settings: nothing pinned.
            workbench.StickyScroll.IsEnabled = false;
            Render(window);
            Assert.Empty(view.StickyLines);
            Assert.False(workbench.CurrentSettings().StickyScroll);

            async Task<EditorView> OpenAt(string directory, string name, string text, int topLine)
            {
                var path = Path.Combine(directory, name);
                await File.WriteAllTextAsync(path, text);
                await workbench.OpenFileAsync(path);
                Dispatcher.UIThread.RunJobs();
                var editor = window.GetVisualDescendants().OfType<EditorView>().Single();
                editor.Offset = new Vector(0, topLine * editor.ScrollSize.Height / 3);
                Render(window);
                return editor;
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void Render(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
    }
}
