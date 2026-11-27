using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Workbench;
using Coder.Core.Extensions;
using Coder.Core.Files;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// An installed extension's snippets are offered by prefix in their language, with no language server:
    /// the card previews the body, accepting inserts it with the file name filled in and the first
    /// placeholder selected, Tab moves to the next one. Other languages don't see them.
    /// </summary>
    [AvaloniaFact]
    public async Task Extension_snippets_complete_by_prefix_with_variables_and_tab_stops()
    {
        var root = Directory.CreateTempSubdirectory("coder-snippets-ui-").FullName;
        using var http = new HttpClient();
        try
        {
            var extension = Path.Combine(root, "installed", "acme.csharp-snippets-1.0.0");
            Directory.CreateDirectory(Path.Combine(extension, "snippets"));
            await File.WriteAllTextAsync(Path.Combine(extension, "package.json"), """
                { "publisher": "acme", "name": "csharp-snippets", "version": "1.0.0", "displayName": "Acme C# Snippets",
                  "contributes": { "snippets": [ { "language": "csharp", "path": "./snippets/csharp.json" } ] } }
                """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(extension, "snippets", "csharp.json"), """
                {
                  "Class": {
                    "prefix": "cls",
                    "body": ["public sealed class ${1:$TM_FILENAME_BASE}", "{", "\tpublic ${2:int} ${3:Value} { get; set; }$0", "}"],
                    "description": "A class named after the file"
                  }
                }
                """, TestContext.Current.CancellationToken);
            var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            var file = Path.Combine(work, "Invoice.cs");
            var notes = Path.Combine(work, "notes.md");
            await File.WriteAllTextAsync(file, "namespace Shop;\n\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(notes, "\n", TestContext.Current.CancellationToken);

            var store = new ExtensionStore(Path.Combine(root, "installed"), http);
            var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore(), extensionStore: store);
            var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
            workbench.OpenFolder(work);
            window.Show();
            await workbench.OpenFileAsync(file);
            var tab = workbench.Editors.ActiveEditor!;
            window.GetVisualDescendants().OfType<Editor.CodeEditor>().First().FocusText();
            tab.Editor.SetCaret(tab.Document.GetLineStart(2));
            Dispatcher.UIThread.RunJobs();

            void Type(string text)
            {
                foreach (var c in text)
                {
                    window.KeyTextInput(c.ToString());
                    Dispatcher.UIThread.RunJobs();
                }
            }

            Type("cl");
            var completion = workbench.Completion;
            await WaitUntil(() => completion.IsOpen, () => "the list did not open");
            var row = Assert.Single(completion.Rows);
            Assert.Equal("cls", row.Item.Label);
            Assert.Equal("Class", row.Item.Detail);
            Assert.Contains("public sealed class ", row.Item.Documentation, StringComparison.Ordinal);
            var details = window.GetVisualDescendants().OfType<Editor.CompletionDetailsCard>().Single();
            Assert.True(details.IsVisible);
            Save(window, "snippet-completion");

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("namespace Shop;\n\npublic sealed class Invoice\n{\n\tpublic int Value { get; set; }\n}", tab.Document.GetText().ReplaceLineEndings("\n"));
            Assert.Equal("Invoice", tab.Document.GetText(tab.Editor.Selection.Start, tab.Editor.Selection.Length));

            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("int", tab.Document.GetText(tab.Editor.Selection.Start, tab.Editor.Selection.Length));
            Type("decimal");
            Assert.Contains("\tpublic decimal Value { get; set; }", tab.Document.GetText(), StringComparison.Ordinal);

            // Markdown has no C# snippets: nothing opens.
            await workbench.OpenFileAsync(notes);
            window.GetVisualDescendants().OfType<Editor.CodeEditor>().First().FocusText();
            Dispatcher.UIThread.RunJobs();
            Type("cls");
            Assert.False(workbench.Completion.IsOpen);
            window.Close();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
