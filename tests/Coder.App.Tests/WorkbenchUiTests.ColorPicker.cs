using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Editor;
using Coder.Core.Colors;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// Color decorators: each color literal in a CSS file gets a swatch before it (not ids, not comments).
    /// Clicking a swatch opens the picker under the literal; changing the color edits the document live as
    /// one undo step, Esc reverts, Enter keeps it. "Show Color Picker" opens it at the caret, and the header
    /// cycles the notation (rgba → hsla).
    /// </summary>
    [AvaloniaFact]
    public async Task Color_literals_get_swatches_and_the_picker_edits_them()
    {
        var folder = Directory.CreateTempSubdirectory("coder-colors-").FullName;
        var file = Path.Combine(folder, "styles.css");
        await File.WriteAllTextAsync(file,
            ".card {\n  color: #ff8800;\n  background: rgba(0, 128, 255, 0.5);\n  border-color: hsl(120, 100%, 25%);\n}\n\n#main { margin: 0 }\n/* #abcdef in a comment */\n",
            TestContext.Current.CancellationToken);
        var (window, workbench) = CreateWindow();
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            await workbench.OpenFileAsync(file);
            var tab = workbench.Editors.ActiveEditor!;
            var editor = window.GetVisualDescendants().OfType<CodeEditor>().First();
            var view = editor.View;
            editor.FocusText();
            Dispatcher.UIThread.RunJobs();

            // Swatches on the three values; none on the id selector or in the comment.
            Assert.Equal(new RgbaColor(255, 136, 0), Assert.Single(view.ColorLiteralsOn(1)).Color);
            Assert.Equal(new RgbaColor(0, 128, 255, 128), Assert.Single(view.ColorLiteralsOn(2)).Color);
            Assert.Equal(new RgbaColor(0, 128, 0), Assert.Single(view.ColorLiteralsOn(3)).Color);
            Assert.Empty(view.ColorLiteralsOn(6));
            Assert.Empty(view.ColorLiteralsOn(7));

            // The swatch takes space before the literal; the caret before '#' sits after it.
            var swatch = view.SwatchBounds(1, 0)!.Value;
            var hash = view.CharacterBounds(tab.Document.GetLineStart(1) + 9);
            Assert.True(swatch.Right <= hash.Left, $"swatch {swatch} should end before the literal at {hash}");
            Assert.True(swatch.Width >= 8);
            Save(window, "color-decorators");

            // Clicking the swatch opens the picker on that literal.
            var point = view.TranslatePoint(swatch.Center, window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var picker = view.OpenColorPicker;
            Assert.NotNull(picker);
            Assert.Equal("#ff8800", picker.Text);
            Assert.True(picker.IsKeyboardFocusWithin);
            var pickerTop = picker.TranslatePoint(default, view)!.Value.Y;
            Assert.True(pickerTop >= hash.Bottom - 1, $"picker at {pickerTop} should open under the line ending at {hash.Bottom}");

            // Live edits; Esc puts the original back.
            picker.SetColor(new RgbaColor(0x33, 0x66, 0xcc));
            Assert.Equal("  color: #3366cc;", tab.Document.GetLineText(1));
            picker.SetColor(new RgbaColor(0x11, 0x22, 0x33, 0x80));
            Assert.Equal("  color: #11223380;", tab.Document.GetLineText(1));
            Save(window, "color-picker");
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(view.OpenColorPicker);
            Assert.Equal("  color: #ff8800;", tab.Document.GetLineText(1));
            Assert.False(tab.Document.IsModified);

            // Again: several changes, Enter keeps the last one, and one undo goes all the way back.
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            picker = view.OpenColorPicker!;
            picker.SetColor(new RgbaColor(255, 0, 0));
            picker.SetColor(new RgbaColor(0, 255, 0));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(view.OpenColorPicker);
            Assert.Equal("  color: #00ff00;", tab.Document.GetLineText(1));
            Assert.Equal(new RgbaColor(0, 255, 0), Assert.Single(view.ColorLiteralsOn(1)).Color); // the swatch follows
            tab.Editor.Undo();
            Assert.Equal("  color: #ff8800;", tab.Document.GetLineText(1));

            // The command opens the picker at the caret; the header cycles rgba → hsla, arrow keys move the hue.
            tab.Editor.SetCaret(tab.Document.GetLineStart(2) + 16);
            await workbench.Commands.ExecuteAsync(EditorView.ShowColorPickerCommand);
            Dispatcher.UIThread.RunJobs();
            picker = view.OpenColorPicker!;
            Assert.Equal("rgba(0, 128, 255, 0.5)", picker.Text);
            picker.CycleFormat();
            Assert.Equal("  background: hsla(210, 100%, 50%, 0.5);", tab.Document.GetLineText(2));
            var hue = picker.GetVisualDescendants().OfType<Control>().First(c => Avalonia.Automation.AutomationProperties.GetName(c) == "Hue");
            hue.Focus(NavigationMethod.Tab);
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Shift);
            Dispatcher.UIThread.RunJobs();
            Assert.Matches(@"^  background: hsla\(24\d, 100%, 50%, 0\.5\);$", tab.Document.GetLineText(2));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(view.OpenColorPicker);
        }
        finally
        {
            window.Close();
        }
    }
}
