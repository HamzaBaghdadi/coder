using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Editor;
using Coder.App.Workbench;
using Coder.Core.Files;
using Coder.Core.Lsp;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    private const string GoProgram = """
        package main

        import "fmt"

        func helper() int {
        	return 42
        }

        func main() {
        	var unused int
        	fmt.Println(helper())
        }

        """;

    /// <summary>
    /// The whole language-server path against a real server (gopls, skipped when not installed):
    /// diagnostics become squiggles and status counts, hovering shows the signature, F12 jumps to the
    /// definition, and typing opens completion whose accepted item replaces the typed prefix.
    /// </summary>
    [AvaloniaFact]
    public async Task Language_server_powers_diagnostics_hover_definition_and_completion()
    {
        var launcher = new ProcessLanguageServerLauncher();
        if (!launcher.IsInstalled(LanguageServerCatalog.For("go")!))
        {
            Assert.Skip("gopls is not installed.");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("coder-gopls-").FullName;
        var mainFile = Path.Combine(folder, "main.go");
        await File.WriteAllTextAsync(Path.Combine(folder, "go.mod"), "module demo\n\ngo 1.21\n");
        await File.WriteAllTextAsync(mainFile, GoProgram.Replace("\r\n", "\n", StringComparison.Ordinal));
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore(), launcher);
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            await workbench.OpenFileAsync(mainFile);
            var tab = workbench.Editors.ActiveEditor!;
            var text = tab.Document.GetText();

            // Diagnostics: gopls reports the unused variable.
            await WaitUntil(() => tab.Diagnostics.Any(d => d.Diagnostic.Message.Contains("unused", StringComparison.Ordinal)),
                () => $"status: {workbench.Languages.StatusText}; message: {workbench.StatusBar.Message}", seconds: 60);
            Assert.True(workbench.Languages.ErrorCount >= 1);
            var marker = tab.Diagnostics.First(d => d.Diagnostic.Message.Contains("unused", StringComparison.Ordinal));
            Assert.Equal("unused", text[marker.Start..marker.End]);

            // Hover: resting the pointer on Println shows its signature.
            var editor = window.GetVisualDescendants().OfType<CodeEditor>().First();
            var println = text.IndexOf("Println", StringComparison.Ordinal) + 2;
            var word = editor.CharacterBounds(println, window)!.Value;
            window.MouseMove(word.Center);
            var card = window.GetVisualDescendants().OfType<HoverCard>().First();
            await WaitUntil(() => card.IsVisible, () => "hover card never appeared");
            Save(window, "lsp-hover");

            // Go to definition: F12 on the call jumps to "func helper".
            tab.Editor.SetCaret(text.IndexOf("helper()", text.IndexOf("Println", StringComparison.Ordinal), StringComparison.Ordinal) + 1);
            await workbench.GoToDefinitionAsync();
            Assert.Equal(4, tab.Editor.CaretPosition.Line);
            Assert.Equal("helper", tab.Editor.SelectedText);

            // Completion: typing "fmt.Pri" opens the list, Enter inserts the chosen function.
            var callLine = text.IndexOf("fmt.Println", StringComparison.Ordinal);
            tab.Editor.SetCaret(text.IndexOf('\n', callLine));
            window.Focus();
            editor.FocusText();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            foreach (var c in "fmt.Pri")
            {
                window.KeyTextInput(c.ToString());
                Dispatcher.UIThread.RunJobs();
            }

            var completion = workbench.Completion;
            await WaitUntil(() => completion.IsOpen && completion.Rows.Any(r => r.Item.Label == "Println"),
                () => $"open: {completion.IsOpen}, rows: {string.Join(", ", completion.Rows.Take(5).Select(r => r.Item.Label))}");
            Assert.All(completion.Rows, row => Assert.StartsWith("Pr", row.Item.Label, StringComparison.Ordinal));
            Save(window, "lsp-completion");

            var chosen = completion.Rows[completion.SelectedIndex].Item.Label;
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var inserted = tab.Document.GetLineText(tab.Editor.CaretPosition.Line).Trim();
            Assert.False(completion.IsOpen);
            Assert.StartsWith($"fmt.{chosen}(", inserted, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }
    }

    /// <summary>
    /// Navigation by symbols and uses, against real gopls: Go to Symbol in Editor (@) lists the file's
    /// functions, Go to Symbol in Workspace (#) finds one in another file, Find All References fills the
    /// References panel with previews (F4 steps through them), and inlay hints name the parameters.
    /// </summary>
    [AvaloniaFact]
    public async Task Symbols_references_and_inlay_hints_with_gopls()
    {
        var launcher = new ProcessLanguageServerLauncher();
        if (!launcher.IsInstalled(LanguageServerCatalog.For("go")!))
        {
            Assert.Skip("gopls is not installed.");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("coder-gosymbols-").FullName;
        var mainFile = Path.Combine(folder, "main.go");
        await File.WriteAllTextAsync(Path.Combine(folder, "go.mod"), "module demo\n\ngo 1.21\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "shapes.go"), "package main\n\nfunc AreaOfSquare(side int) int {\n\treturn side * side\n}\n");
        await File.WriteAllTextAsync(mainFile, "package main\n\nfunc add(left int, right int) int {\n\treturn left + right\n}\n\nfunc main() {\n\ttotal := add(2, 3)\n\t_ = add(total, AreaOfSquare(4))\n}\n");
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore(), launcher);
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            await workbench.OpenFileAsync(mainFile);
            var tab = workbench.Editors.ActiveEditor!;
            await WaitUntil(() => workbench.Languages.Supports(tab, c => c.HasDocumentSymbols), () => $"status: {workbench.Languages.StatusText}", seconds: 60);

            // @: the file's symbols, in file order.
            workbench.Palette.Open("@");
            await WaitUntil(() => workbench.Palette.Items.Any(i => i.Label == "add"), () => string.Join(", ", workbench.Palette.Items.Select(i => i.Label)));
            Assert.Equal(["add", "main"], workbench.Palette.Items.Select(i => i.Label));
            Assert.StartsWith("function", workbench.Palette.Items[0].Detail, StringComparison.Ordinal);
            await workbench.Palette.AcceptAsync(workbench.Palette.Items[0]);
            Assert.Equal("add", tab.Editor.SelectedText);

            // #: a symbol from another file, by name.
            workbench.Palette.Open("#");
            workbench.Palette.Query = "#AreaOfSq";
            await WaitUntil(() => workbench.Palette.Items.Any(i => i.Label == "AreaOfSquare"), () => string.Join(", ", workbench.Palette.Items.Select(i => i.Label)), seconds: 30);
            Assert.Equal("shapes.go", workbench.Palette.Items.First(i => i.Label == "AreaOfSquare").Hint);
            await workbench.Palette.AcceptAsync(workbench.Palette.Items.First(i => i.Label == "AreaOfSquare"));
            Assert.EndsWith("shapes.go", workbench.Editors.ActiveEditor!.FilePath, StringComparison.Ordinal);

            // Shift+F12: the References panel, the declaration and both calls, each with its line.
            await workbench.OpenFileAsync(mainFile);
            tab.Editor.SetCaret(tab.Document.GetText().IndexOf("add(", StringComparison.Ordinal) + 1);
            await workbench.FindReferencesAsync();
            Assert.Equal(PanelView.References, workbench.ActivePanel);
            var references = workbench.References;
            Assert.Equal(3, references.Count);
            Assert.StartsWith("3 references to \"add\" in 1 file", references.Title, StringComparison.Ordinal);
            var call = references.Rows.OfType<Presentation.LanguageFeatures.ReferenceItemRow>().First(r => r.LineNumber == 8);
            Assert.Equal("total := add(2, 3)", call.Preview);
            Assert.Equal("add", call.Preview.Substring(call.MatchStart, call.MatchLength));
            await references.StepAsync(1);
            await references.StepAsync(1);
            Assert.Equal(7, tab.Editor.CaretPosition.Line); // the second reference: line 8, the first call
            Save(window, "references");

            // Inlay hints: gopls names the arguments ("left:") and the inferred type of total.
            await WaitUntil(() => tab.InlayHintsOn(7).Count > 0, () => "no inlay hints", seconds: 30);
            Assert.Contains(tab.InlayHintsOn(7), h => h.Label.Contains("left", StringComparison.Ordinal));
            Save(window, "inlay-hints");
            await workbench.Commands.Get(Presentation.Commands.CommandIds.ToggleInlayHints).Execute();
            Assert.Empty(tab.InlayHintsOn(7));
        }
        finally
        {
            window.Close();
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }
    }

    [AvaloniaFact]
    public async Task Opening_a_file_whose_server_is_missing_says_how_to_install_it()
    {
        var folder = Directory.CreateTempSubdirectory("coder-noserver-").FullName;
        var script = Path.Combine(folder, "app.py");
        await File.WriteAllTextAsync(script, "print('hi')\n");
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore(), new NothingInstalled());
        try
        {
            await workbench.OpenFileAsync(script);

            Assert.Contains("npm install -g pyright", workbench.StatusBar.Message, StringComparison.Ordinal);
            Assert.Equal(string.Empty, workbench.Languages.StatusText);
            Assert.False(workbench.Languages.CanComplete(workbench.Editors.ActiveEditor!));
        }
        finally
        {
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }
    }

    private sealed class NothingInstalled : ILanguageServerLauncher
    {
        public bool IsInstalled(LanguageServerDefinition server) => false;

        public LanguageClient Launch(LanguageServerDefinition server, string rootPath) => throw new InvalidOperationException("Not installed.");
    }

    /// <summary>A server's child processes (gopls runs "go list") can hold the folder for a moment after shutdown.</summary>
    private static void DeleteEventually(string folder)
    {
        for (var attempt = 0; attempt < 20 && Directory.Exists(folder); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal); // git object files are read-only
                }

                Directory.Delete(folder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
    }
}
