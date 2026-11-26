using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Workbench;
using Coder.Core.Files;
using Coder.Core.Lsp;
using Coder.Presentation.LanguageFeatures;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// Regression: in an HTML file, typing after &lt;/html&gt; and deleting it with Delete or Backspace froze
    /// the app. Both keys, on the same line as the tag and on a new line after it.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(" hello", false)]
    [InlineData(" hello", true)]
    [InlineData("\nhello", false)]
    [InlineData("\nhello", true)]
    public async Task Deleting_text_after_the_closing_html_tag_does_not_freeze(string typed, bool forwardDelete)
    {
        var folder = Directory.CreateTempSubdirectory("coder-html-").FullName;
        var file = Path.Combine(folder, "index.html");
        await File.WriteAllTextAsync(file, "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"UTF-8\">\n  <title>Demo</title>\n</head>\n<body>\n  <p>Hello</p>\n</body>\n</html>");
        var (window, workbench) = CreateWindow();
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            await workbench.OpenFileAsync(file);
            var tab = workbench.Editors.ActiveEditor!;
            var editor = window.GetVisualDescendants().OfType<Editor.CodeEditor>().First();
            editor.FocusText();
            tab.Editor.SetCaret(tab.Document.Length);
            Dispatcher.UIThread.RunJobs();

            foreach (var c in typed)
            {
                if (c == '\n')
                {
                    window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                }
                else
                {
                    window.KeyTextInput(c.ToString());
                }

                Dispatcher.UIThread.RunJobs();
            }

            if (forwardDelete)
            {
                tab.Editor.SetCaret(tab.Document.Length - "hello".Length);
            }

            for (var i = 0; i < typed.Length; i++)
            {
                window.KeyPressQwerty(forwardDelete ? PhysicalKey.Delete : PhysicalKey.Backspace, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                Save(window, "html-delete");
            }

            Assert.EndsWith("</html>", tab.Document.GetText().TrimEnd(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }
    }

    /// <summary>
    /// A refactoring's workspace edit, step by step: create a file and fill it, rename an open file (its
    /// tab follows), delete a file whose tab is clean (the tab closes), and stop at a step that can't be
    /// done (creating a file that exists), keeping what was done before it.
    /// </summary>
    [AvaloniaFact]
    public async Task Workspace_edits_create_rename_and_delete_files()
    {
        var folder = Directory.CreateTempSubdirectory("coder-fileops-").FullName;
        var (oldName, newName, doomed, created) = (Path.Combine(folder, "a.go"), Path.Combine(folder, "b.go"), Path.Combine(folder, "old.go"), Path.Combine(folder, "util", "helper.go"));
        await File.WriteAllTextAsync(oldName, "package main\n");
        await File.WriteAllTextAsync(doomed, "package main\n");
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore());
        try
        {
            await workbench.OpenFileAsync(oldName);
            var renamedTab = workbench.Editors.ActiveEditor!;
            await workbench.OpenFileAsync(doomed);
            var applier = new WorkspaceEditApplier(new LocalFileSystem(), workbench.Editors, workbench.Languages);

            var start = new TextRange(new Core.Text.TextPosition(0, 0), new Core.Text.TextPosition(0, 0));
            var changed = await applier.ApplyAsync(new WorkspaceEdit(
            [
                new CreateFile(created, Overwrite: false, IgnoreIfExists: false),
                new FileEdit(created, [new TextReplacement(start, "package util\n")]),
                new RenameFile(oldName, newName, Overwrite: false, IgnoreIfExists: false),
                new DeleteFile(doomed, Recursive: false, IgnoreIfNotExists: false),
            ]));

            Assert.Equal(3, changed);
            Assert.Equal("package util\n", (await File.ReadAllTextAsync(created)).ReplaceLineEndings("\n")); // a new file gets the platform's line endings
            Assert.False(File.Exists(oldName));
            Assert.True(File.Exists(newName));
            Assert.Equal(newName, renamedTab.FilePath);
            Assert.False(File.Exists(doomed));
            Assert.Null(workbench.Editors.FindTab(doomed));

            // Creating a file that exists fails the edit at that step.
            await Assert.ThrowsAsync<IOException>(() => applier.ApplyAsync(new WorkspaceEdit([new CreateFile(created, Overwrite: false, IgnoreIfExists: false)])));
            Assert.Equal("package util\n", (await File.ReadAllTextAsync(created)).ReplaceLineEndings("\n")); // not emptied
        }
        finally
        {
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }
    }

    private const string MainGo = """
        package main

        import (
        	"fmt"
        	"os"
        )

        func main() {
        	x:=helper()
        	fmt.Println(x)
        }

        """;

    private const string HelperGo = """
        package main

        func helper() int {
        	return helper2() + 1
        }

        func helper2() int { return 41 }

        func useHelper() int { return helper() }

        """;

    /// <summary>
    /// Rename, references, parameter hints, formatting, quick fixes and the Problems panel against a
    /// real gopls (skipped when it is not installed), on a two-file package where one file stays closed.
    /// </summary>
    [AvaloniaFact]
    public async Task Language_features_rename_references_signature_format_and_fix()
    {
        var launcher = new ProcessLanguageServerLauncher();
        if (!launcher.IsInstalled(LanguageServerCatalog.For("go")!))
        {
            Assert.Skip("gopls is not installed.");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("coder-lspfeatures-").FullName;
        var mainFile = Path.Combine(folder, "main.go");
        var helperFile = Path.Combine(folder, "helper.go");
        await File.WriteAllTextAsync(Path.Combine(folder, "go.mod"), "module demo\n\ngo 1.21\n");
        await File.WriteAllTextAsync(mainFile, MainGo.Replace("\r\n", "\n", StringComparison.Ordinal));
        await File.WriteAllTextAsync(helperFile, HelperGo.Replace("\r\n", "\n", StringComparison.Ordinal));
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore(), launcher);
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            await workbench.OpenFileAsync(mainFile);
            var tab = workbench.Editors.ActiveEditor!;
            string Text() => tab.Document.GetText();

            // Problems: the unused import is reported and listed; activating it selects it.
            await WaitUntil(() => workbench.Problems.Rows.OfType<ProblemItemRow>().Any(r => r.Diagnostic.Message.Contains("\"os\"", StringComparison.Ordinal)),
                () => $"problems: {string.Join(" | ", workbench.Problems.Rows.OfType<ProblemItemRow>().Select(r => r.Diagnostic.Message))}", seconds: 60);
            workbench.ShowPanel(PanelView.Problems);
            Dispatcher.UIThread.RunJobs();
            Save(window, "problems");
            await workbench.Problems.ActivateAsync(workbench.Problems.Rows.OfType<ProblemItemRow>().First(r => r.Diagnostic.Message.Contains("\"os\"", StringComparison.Ordinal)));
            Assert.Equal("\"os\"", tab.Editor.SelectedText);

            // Quick fix on it: the offered fix removes the import.
            var actions = await workbench.Languages.CodeActionsAsync(tab, tab.Editor.Selection.Start, tab.Editor.Selection.End, CancellationToken.None);
            Assert.NotNull(actions);
            await workbench.LanguageCommands.QuickFixAsync();
            Assert.True(workbench.Palette.IsOpen, $"actions: {string.Join(", ", actions.Select(a => a.Title))}");
            await workbench.Palette.AcceptAsync(workbench.Palette.Items.First(i => i.Label.Contains("os", StringComparison.OrdinalIgnoreCase) || i.Label.Contains("import", StringComparison.OrdinalIgnoreCase)));
            await WaitUntil(() => !Text().Contains("\"os\"", StringComparison.Ordinal), () => Text());

            // Format Document: gofmt spacing.
            await workbench.LanguageCommands.FormatDocumentAsync();
            await WaitUntil(() => Text().Contains("x := helper()", StringComparison.Ordinal), () => Text());

            // References: helper is declared once and used twice across both files.
            var call = Text().IndexOf("helper()", StringComparison.Ordinal) + 1;
            var references = await workbench.Languages.FindReferencesAsync(tab, call, CancellationToken.None);
            Assert.Equal(3, references!.Count);
            Assert.Equal(2, references.Count(r => r.Path.EndsWith("helper.go", StringComparison.Ordinal)));

            // Rename: the open file changes in the editor, the closed one on disk.
            await workbench.LanguageCommands.RenameAsync(tab, call, "compute");
            Assert.Contains("x := compute()", Text(), StringComparison.Ordinal);
            var helperText = await File.ReadAllTextAsync(helperFile);
            Assert.Contains("func compute() int", helperText, StringComparison.Ordinal);
            Assert.Contains("return compute() }", helperText, StringComparison.Ordinal);
            Assert.Contains("helper2()", helperText, StringComparison.Ordinal); // a different symbol is untouched

            // The server knows about the closed file's change: no stale "undefined: compute". The server
            // needs a moment to re-check and publish, so let it settle before looking.
            await Settle(TimeSpan.FromSeconds(4));
            Assert.DoesNotContain(workbench.Problems.Rows.OfType<ProblemItemRow>(), r => r.Diagnostic.Message.Contains("compute", StringComparison.Ordinal));

            // Signature help: typing "(" after fmt.Printf shows its parameters.
            tab.Editor.SetCaret(Text().IndexOf("fmt.Println(x)", StringComparison.Ordinal) + "fmt.Println(x)".Length);
            tab.Editor.InsertNewLine();
            window.Focus();
            foreach (var c in "fmt.Printf(")
            {
                window.KeyTextInput(c.ToString());
                Dispatcher.UIThread.RunJobs();
            }

            workbench.Completion.Close();
            await WaitUntil(() => workbench.SignatureHelp.Current?.Signature.Label.Contains("format string", StringComparison.Ordinal) == true,
                () => $"help: {workbench.SignatureHelp.Current?.Signature.Label ?? "none"}");
            Assert.Equal(0, workbench.SignatureHelp.Current!.Value.Parameter);
            window.KeyTextInput("\"%d\",");
            Dispatcher.UIThread.RunJobs();
            await WaitUntil(() => workbench.SignatureHelp.Current?.Parameter == 1, () => $"active: {workbench.SignatureHelp.Current?.Parameter}");
            Save(window, "signature-help");
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(workbench.SignatureHelp.IsOpen);
        }
        finally
        {
            window.Close();
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }
    }

    /// <summary>A server configured in settings.json replaces the built-in one for its languages.</summary>
    [AvaloniaFact]
    public async Task Language_servers_from_settings_replace_the_built_in_ones()
    {
        var folder = Directory.CreateTempSubdirectory("coder-customserver-").FullName;
        var settingsFile = Path.Combine(folder, "settings.json");
        await File.WriteAllTextAsync(settingsFile, """
            {
              "languageServers": [
                { "name": "pylsp", "languages": ["python"], "command": "pylsp-not-really-installed", "arguments": ["--check"] }
              ]
            }
            """);
        var script = Path.Combine(folder, "app.py");
        await File.WriteAllTextAsync(script, "print('hi')\n");
        var launcher = new RecordingLauncher();
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new Services.JsonSettingsStore(settingsFile), launcher);
        try
        {
            await workbench.OpenFileAsync(script);

            var server = Assert.Single(launcher.Asked);
            Assert.Equal("pylsp", server.Name);
            Assert.Equal(["--check"], server.Arguments);
            Assert.Contains("pylsp-not-really-installed", workbench.StatusBar.Message, StringComparison.Ordinal);
        }
        finally
        {
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }
    }

    private sealed class RecordingLauncher : ILanguageServerLauncher
    {
        public List<LanguageServerDefinition> Asked { get; } = [];

        public bool IsInstalled(LanguageServerDefinition server)
        {
            Asked.Add(server);
            return false;
        }

        public LanguageClient Launch(LanguageServerDefinition server, string rootPath) => throw new InvalidOperationException("Not installed.");
    }

    /// <summary>Keeps the UI running for a while, so asynchronous server messages arrive and apply.</summary>
    private static async Task Settle(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
