using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Theme;
using Coder.App.Workbench;
using Coder.Core.Extensions;
using Coder.Core.Files;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// The Extensions view lists what a registry offers; Install downloads and verifies it, moves it to
    /// Installed and offers its themes; picking one applies it; Uninstall removes it again.
    /// </summary>
    [AvaloniaFact]
    public async Task Extensions_view_installs_a_theme_applies_it_and_uninstalls_it()
    {
        var root = Directory.CreateTempSubdirectory("coder-store-").FullName;
        using var http = new HttpClient();
        try
        {
            var package = ThemePackage(root, "acme", "ember", "Acme Ember");
            var sha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package, TestContext.Current.CancellationToken)));
            var index = Path.Combine(root, "index.json");
            await File.WriteAllTextAsync(index, $$"""{ "extensions": [ { "publisher": "acme", "name": "ember", "displayName": "Acme Ember", "version": "1.0.0", "description": "Warm dark colors", "download": "{{Path.GetFileName(package)}}", "sha256": "{{sha}}", "downloads": 1200 } ] }""", TestContext.Current.CancellationToken);

            var store = new ExtensionStore(Path.Combine(root, "installed"), http);
            var themes = new ThemeCatalog([store.Folder], null);
            var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore(), themes: themes,
                extensionStore: store, extensionSources: [new CoderRegistrySource(http, index)]);
            var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
            window.Show();

            Button(window, "Extensions (Ctrl+Shift+X)").Command!.Execute(null);
            await WaitUntil(() => workbench.Extensions.Results.Count == 1, () => workbench.Extensions.Message);
            Assert.True(window.GetVisualDescendants().OfType<ExtensionsView>().Single().IsEffectivelyVisible);
            Save(window, "extensions-marketplace");

            Button(window, "Install Acme Ember").Command!.Execute(null);
            await WaitUntil(() => workbench.Extensions.Installed.Count == 1 && workbench.Palette.IsOpen, () => workbench.Extensions.Message);
            Assert.Empty(workbench.Extensions.Results);

            // The picker offers just this extension's theme; choosing it applies it.
            var choice = Assert.Single(workbench.Palette.Items);
            Assert.Equal("Acme Ember", choice.Label);
            await choice.Accept();
            Assert.Equal("vscode:acme.ember/Acme Ember", workbench.ColorTheme);
            workbench.Palette.Close();
            Dispatcher.UIThread.RunJobs();
            Save(window, "extensions-installed");

            Button(window, "Uninstall Acme Ember").Command!.Execute(null);
            await WaitUntil(() => workbench.Extensions.Installed.Count == 0 && workbench.Extensions.Results.Count == 1);
            Assert.Empty(store.Installed());
            window.Close();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A .vsix-layout package with one dark theme.</summary>
    private static string ThemePackage(string folder, string publisher, string name, string label)
    {
        var path = Path.Combine(folder, $"{publisher}.{name}.vsix");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Write(string entry, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        Write("extension/package.json", $$"""
            { "publisher": "{{publisher}}", "name": "{{name}}", "version": "1.0.0", "displayName": "{{label}}",
              "contributes": { "themes": [ { "label": "{{label}}", "uiTheme": "vs-dark", "path": "./theme.json" } ] } }
            """);
        Write("extension/theme.json", """{ "type": "dark", "colors": { "editor.background": "#1a1210", "foreground": "#f0d8c8" }, "tokenColors": [] }""");
        return path;
    }
}
