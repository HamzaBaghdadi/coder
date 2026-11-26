using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Coder.App.Workbench;
using Coder.Core.Files;
using Coder.Core.Lsp;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>Shared by test runs, so the servers are downloaded once rather than every run.</summary>
    private static readonly string TestServersFolder = Path.Combine(Path.GetTempPath(), "coder-test-servers");

    /// <summary>
    /// Tailwind CSS IntelliSense in a Tailwind project: it runs beside the HTML server (installed on first
    /// use), its class names come in the same completion list as HTML's, and hovering a class shows its CSS.
    /// A project without Tailwind doesn't start it.
    /// </summary>
    [AvaloniaFact]
    public async Task Tailwind_completes_and_explains_classes_in_tailwind_projects()
    {
        var managed = new ManagedServers([], TestServersFolder);
        if (!managed.CanInstall && managed.Find(ManagedTools.TailwindServer) is null)
        {
            Assert.Skip("Node.js and npm are needed to install Tailwind CSS IntelliSense.");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("coder-tailwind-").FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "tailwind.config.js"), "module.exports = { content: ['./*.html'], theme: { extend: {} }, plugins: [] };\n");
        var page = Path.Combine(folder, "index.html");
        await File.WriteAllTextAsync(page, "<!DOCTYPE html>\n<html>\n<body>\n  <p class=\"text-center\">x</p>\n  \n</body>\n</html>\n");
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore(), new ProcessLanguageServerLauncher(managed), managed: managed);
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            await workbench.OpenFileAsync(page);
            var tab = workbench.Editors.ActiveEditor!;
            tab.Editor.SetCaret(tab.Document.GetLineStart(4) + 2);
            foreach (var c in "<div class=\"bg-re")
            {
                tab.Editor.TypeText(c.ToString()); // as typed: the quote closes itself
            }

            Assert.Contains("class=\"bg-re\"", tab.Document.GetText(), StringComparison.Ordinal);

            IReadOnlyList<Coder.Core.Lsp.CompletionItem> items = [];
            await WaitUntil(() =>
            {
                Dispatcher.UIThread.RunJobs();
                var task = workbench.Languages.CompleteAsync(tab, tab.Editor.Selection.Active, null, CancellationToken.None);
                while (!task.IsCompleted)
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(20);
                }

                items = task.Result?.Items ?? [];
                return items.Any(i => i.Label == "bg-red-500");
            }, () => $"servers: {string.Join(", ", workbench.Languages.ServerStates)}; items: {items.Count}", seconds: 90);

            // Hovering a class shows the CSS it stands for.
            var text = tab.Document.GetText();
            var hover = await workbench.Languages.HoverAsync(tab, text.IndexOf("text-center", StringComparison.Ordinal) + 2, CancellationToken.None);
            Assert.Contains("text-align: center", hover?.Text ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }

        Assert.False(Core.Lsp.LanguageServerCatalog.UsesTailwind(Directory.CreateTempSubdirectory("coder-no-tailwind-").FullName));
    }

    /// <summary>
    /// The web languages work out of the box: opening an HTML file installs Coder's HTML, CSS and JSON
    /// servers (npm, first use only) and completes tags; CSS completes properties; TypeScript reports a
    /// type error and understands JSX in .tsx; Format Document formats with Prettier.
    /// </summary>
    [AvaloniaFact]
    public async Task Web_languages_work_out_of_the_box()
    {
        var managed = new ManagedServers([], TestServersFolder);
        if (!managed.CanInstall && ManagedTools.All.Any(tool => managed.Find(tool) is null))
        {
            Assert.Skip("Node.js and npm are needed to install the web language servers.");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("coder-web-").FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "index.html"), "<!DOCTYPE html>\n<html>\n<body>\n  \n</body>\n</html>\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "site.css"), "body {\n  \n}\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "app.ts"), "const count: number = \"three\";\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "view.tsx"), "export const View = () => <div className=\"x\">hi</div>;\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "tsconfig.json"), "{ \"compilerOptions\": { \"jsx\": \"react-jsx\", \"strict\": true, \"noEmit\": true } }\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "messy.js"), "const   a={b:1,   c:[1,2,3]}\nfunction f( x ){return x*2}\n");

        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore(), new ProcessLanguageServerLauncher(managed), managed: managed);
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        try
        {
            workbench.OpenFolder(folder);
            window.Show();

            // HTML: the server installs itself, then completes "<di" with div.
            await workbench.OpenFileAsync(Path.Combine(folder, "index.html"));
            var html = workbench.Editors.ActiveEditor!;
            await WaitUntil(() => workbench.Languages.CanComplete(html), () => $"status: {workbench.Languages.StatusText}; message: {workbench.StatusBar.Message}", seconds: 240);
            html.Editor.SetCaret(html.Document.GetLineStart(3) + 2);
            html.Editor.TypeText("<di");
            var tags = await workbench.Languages.CompleteAsync(html, html.Editor.Selection.Active, null, CancellationToken.None);
            Assert.Contains(tags!.Items, item => item.Label == "div");

            // CSS: properties.
            await workbench.OpenFileAsync(Path.Combine(folder, "site.css"));
            var css = workbench.Editors.ActiveEditor!;
            await WaitUntil(() => workbench.Languages.CanComplete(css), () => $"status: {workbench.Languages.StatusText}; message: {workbench.StatusBar.Message}", seconds: 60);
            css.Editor.SetCaret(css.Document.GetLineStart(1) + 2);
            css.Editor.TypeText("col");
            var properties = await workbench.Languages.CompleteAsync(css, css.Editor.Selection.Active, null, CancellationToken.None);
            Assert.Contains(properties!.Items, item => item.Label == "color");

            // TypeScript: a type error is reported; JSX in .tsx is not an error.
            await workbench.OpenFileAsync(Path.Combine(folder, "app.ts"));
            var ts = workbench.Editors.ActiveEditor!;
            await WaitUntil(() => ts.Diagnostics.Any(d => d.Diagnostic.Message.Contains("not assignable", StringComparison.Ordinal)),
                () => $"status: {workbench.Languages.StatusText}; message: {workbench.StatusBar.Message}; diagnostics: {string.Join(" | ", ts.Diagnostics.Select(d => d.Diagnostic.Message))}", seconds: 240);
            await workbench.OpenFileAsync(Path.Combine(folder, "view.tsx"));
            var tsx = workbench.Editors.ActiveEditor!;
            await WaitUntil(() => workbench.Languages.CanComplete(tsx), () => workbench.Languages.StatusText, seconds: 60);
            await Task.Delay(3000);
            Dispatcher.UIThread.RunJobs();
            // JSX parses (sent as "typescriptreact"): no syntax errors (TS1xxx) and no "JSX is not enabled" (17004).
            // Errors about React's types are right: the test project has no node_modules.
            Assert.NotEmpty(tsx.Diagnostics);
            Assert.DoesNotContain(tsx.Diagnostics, d => d.Diagnostic.Code is { Length: 4 } code && code.StartsWith('1'));
            Assert.DoesNotContain(tsx.Diagnostics, d => d.Diagnostic.Code == "17004");
            Save(window, "web-typescript");

            // Prettier: Format Document.
            await workbench.OpenFileAsync(Path.Combine(folder, "messy.js"));
            var js = workbench.Editors.ActiveEditor!;
            await workbench.Commands.Get(Presentation.Commands.CommandIds.FormatDocument).Execute();
            await WaitUntil(() => workbench.StatusBar.Message.Contains("Prettier", StringComparison.Ordinal) && !workbench.StatusBar.Message.EndsWith('…'),
                () => workbench.StatusBar.Message, seconds: 240);
            Assert.Equal("const a = { b: 1, c: [1, 2, 3] };\nfunction f(x) {\n  return x * 2;\n}\n", js.Document.GetText());
            Assert.True(js.Document.CanUndo, "formatting is one undo step");
        }
        finally
        {
            window.Close();
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }
    }
}
