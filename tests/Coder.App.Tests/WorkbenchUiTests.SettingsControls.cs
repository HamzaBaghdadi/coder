using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Controls;
using Coder.App.Editor;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// The settings page uses the app's own controls (no stock NumericUpDown) and offers the newer settings:
    /// the secondary side bar's width, the code font family and the line height, which apply live.
    /// </summary>
    [AvaloniaFact]
    public void Settings_page_number_fields_and_editor_font_settings_apply_live()
    {
        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        workbench.OpenSettingsPage();
        Dispatcher.UIThread.RunJobs();

        T Named<T>(string name) where T : Control =>
            window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);

        Assert.Empty(window.GetVisualDescendants().OfType<NumericUpDown>());

        // The number field: typing commits on Enter, Up steps, and values stay within the setting's range.
        var width = Named<NumberBox>("Secondary Side Bar Default Width");
        width.Text.Text = "500";
        width.Text.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
        Assert.Equal(500, workbench.SecondarySideBarDefaultWidth);
        width.Text.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Up });
        Assert.Equal(501, workbench.SecondarySideBarDefaultWidth);
        width.Text.Text = "99999";
        width.Text.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
        Assert.Equal(900, workbench.SecondarySideBarDefaultWidth);
        Assert.Equal("900", width.Text.Text);

        // Font family and line height reach the editor.
        var family = Named<ComboBox>("Font Family");
        Assert.Equal("Default", Assert.IsType<Presentation.Settings.SettingOption>(family.SelectedItem).Label);
        workbench.EditorFontFamily = "Consolas";
        workbench.EditorLineHeight = 1.8;
        Dispatcher.UIThread.RunJobs();
        var editor = window.GetVisualDescendants().OfType<CodeEditor>().Single();
        Assert.Equal("Consolas", editor.GetValue(CodeEditor.EditorFontFamilyProperty));
        Assert.Equal(1.8, editor.GetValue(CodeEditor.EditorLineHeightProperty));

        // The color theme list holds at least the built-in themes (installed ones join as they are found).
        Assert.True(Named<ComboBox>("Color Theme").ItemCount >= 5);
        Save(window, "settings-controls");
        workbench.EditorFontFamily = string.Empty;
        workbench.EditorLineHeight = 1.5;
        window.Close();
    }

    /// <summary>
    /// Indentation follows the file (or the settings when detection is off); saving trims trailing whitespace
    /// and adds a final newline when asked; "after a delay" auto save writes the file; the caret takes its style.
    /// </summary>
    [AvaloniaFact]
    public async Task Text_editor_settings_indentation_save_cleanups_auto_save_and_caret()
    {
        var folder = Directory.CreateTempSubdirectory("coder-editor-settings-").FullName;
        var file = Path.Combine(folder, "two.cs");
        await File.WriteAllTextAsync(file, "class A\n{\n  void B()\n  {\n    x();\n  }\n}\n", TestContext.Current.CancellationToken);
        var (window, workbench) = CreateWindow();
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            await workbench.OpenFileAsync(file);
            Dispatcher.UIThread.RunJobs();
            var tab = workbench.Editors.ActiveEditor!;

            // Detected from the text: 2 spaces. With detection off, the settings (4 spaces) apply.
            Assert.Equal("Spaces: 2", workbench.StatusBar.IndentText);
            workbench.Settings.DetectIndentation = false;
            workbench.Editors.ReapplyOptions();
            Assert.Equal("Spaces: 4", workbench.StatusBar.IndentText);
            workbench.Settings.DetectIndentation = true;
            workbench.Editors.ReapplyOptions();

            // Save clean-ups.
            workbench.Settings.TrimTrailingWhitespace = true;
            workbench.Settings.InsertFinalNewline = true;
            tab.Editor.ReplaceText("class A   \n{\n}");
            await workbench.Editors.SaveAsync(tab);
            Assert.Equal("class A\n{\n}\n", (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Replace("\r\n", "\n", StringComparison.Ordinal));

            // Auto save after a short delay.
            workbench.Settings.AutoSave = "afterDelay";
            workbench.Settings.AutoSaveDelay = 100;
            tab.Editor.ReplaceText("class B\n{\n}\n");
            for (var i = 0; i < 100 && tab.IsDirty; i++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.False(tab.IsDirty);
            Assert.StartsWith("class B", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);

            // The caret style and blinking reach the editor.
            workbench.CursorStyle = "block";
            workbench.CursorBlinking = false;
            Dispatcher.UIThread.RunJobs();
            var editor = window.GetVisualDescendants().OfType<CodeEditor>().Single();
            Assert.Equal("block", editor.GetValue(CodeEditor.CursorStyleProperty));
            Assert.False(editor.GetValue(CodeEditor.CursorBlinkingProperty));
            Save(window, "editor-block-caret");
        }
        finally
        {
            workbench.Settings.AutoSave = "off";
            workbench.CursorStyle = "line";
            workbench.CursorBlinking = true;
            window.Close();
            Directory.Delete(folder, recursive: true);
        }
    }
}
