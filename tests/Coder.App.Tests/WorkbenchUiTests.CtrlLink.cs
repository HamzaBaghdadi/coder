using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Editor;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// Ctrl+hover underlines the symbol under the pointer. When Ctrl+Click then opens a shorter file (go to
    /// definition), the underline from the first file must not be drawn into the second (it crashed).
    /// </summary>
    [AvaloniaFact]
    public async Task Ctrl_hover_underline_does_not_outlive_its_document()
    {
        var folder = Directory.CreateTempSubdirectory("coder-ctrllink-").FullName;
        try
        {
            var longFile = Path.Combine(folder, "long.ts");
            var shortFile = Path.Combine(folder, "short.ts");
            await File.WriteAllTextAsync(longFile, new string('a', 300) + "\n" + string.Concat(Enumerable.Repeat("const value = other + another;\n", 8)), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(shortFile, "x\n", TestContext.Current.CancellationToken);
            var (window, workbench) = CreateWindow();
            window.Show();
            await workbench.OpenFileAsync(longFile);
            Dispatcher.UIThread.RunJobs();
            var view = window.GetVisualDescendants().OfType<EditorView>().Single();
            view.CanGoToDefinition = () => true;

            // Hover a word with Ctrl held (its offsets are past the end of the short file).
            var target = view.TranslatePoint(new Point(view.Bounds.Width / 2, 10), window)!.Value;
            view.Focus();
            window.MouseMove(target);
            window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(view.CtrlLink);

            await workbench.OpenFileAsync(shortFile); // as Ctrl+Click going to a definition elsewhere does
            Dispatcher.UIThread.RunJobs();
            Assert.Null(view.CtrlLink);
            Save(window, "ctrl-link-after-jump"); // renders: used to throw
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
