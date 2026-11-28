using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Workbench;
using Coder.Core.Extensions;
using Coder.Core.Files;
using Coder.Presentation.Editors;
using Coder.Presentation.Extensions;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// "Open Preview" on a Markdown file shows it rendered in a tab beside it; typing in the file updates the
    /// preview; "Open Source" goes back. The button only shows on files that have a preview.
    /// </summary>
    [AvaloniaFact]
    public async Task Markdown_preview_renders_follows_typing_and_switches_back_to_source()
    {
        var folder = Directory.CreateTempSubdirectory("coder-preview-").FullName;
        var file = Path.Combine(folder, "README.md");
        await File.WriteAllBytesAsync(Path.Combine(folder, "logo.bmp"), Bmp2x2(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(file, """
            # Coder

            A **fast** editor. <br> See [the guide](docs/guide.md) or [install](#install).

            <p align="center"><img src="logo.bmp" alt="Logo"></p>

            ## Install

            - Download the installer
            - Run it

            ```csharp
            var x = 1;
            ```

            | Key | Action |
            | --- | --- |
            | Ctrl+P | Go to file |
            """, TestContext.Current.CancellationToken);
        var (window, workbench) = CreateWindow();
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            await workbench.OpenFileAsync(file);
            Dispatcher.UIThread.RunJobs();
            Assert.True(workbench.CanPreviewActive);

            await workbench.Commands.Get(Presentation.Commands.CommandIds.OpenPreview).Execute();
            Dispatcher.UIThread.RunJobs();
            var preview = Assert.IsType<PreviewTabViewModel>(workbench.Editors.ActiveTab);
            Assert.Equal("Preview README.md", preview.Title);
            Assert.Equal(PreviewKind.Markdown, preview.Kind);

            var view = window.GetVisualDescendants().OfType<PreviewView>().Single();
            Assert.True(view.IsEffectivelyVisible);
            Assert.Contains("Coder", view.Markdown.Text, StringComparison.Ordinal);
            Assert.Contains("Download the installer", view.Markdown.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("<br>", view.Markdown.Text, StringComparison.Ordinal);
            await WaitUntil(() => view.Markdown.GetVisualDescendants().OfType<Image>().Any(image => image.Source is not null)); // the local image loaded
            Save(window, "markdown-preview");

            // Typing in the file shows in the preview a moment later.
            var source = workbench.Editors.FindTab(file)!;
            source.Editor.ReplaceText("# Renamed\n\nNew text.\n");
            for (var i = 0; i < 50 && !view.Markdown.Text.Contains("Renamed", StringComparison.Ordinal); i++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Contains("Renamed", view.Markdown.Text, StringComparison.Ordinal);

            // A long document scrolls, with the mouse wheel too.
            source.Editor.ReplaceText(string.Concat(Enumerable.Range(1, 120).Select(i => $"Paragraph {i} of a long document.\n\n")));
            await WaitUntil(() => view.Markdown.Text.Contains("Paragraph 120", StringComparison.Ordinal));
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.Markdown.Extent.Height > view.Markdown.Viewport.Height + 100,
                $"extent {view.Markdown.Extent.Height}, viewport {view.Markdown.Viewport.Height}");
            var center = view.Markdown.TranslatePoint(new Avalonia.Point(view.Markdown.Bounds.Width / 2, view.Markdown.Bounds.Height / 2), window)!.Value;
            window.MouseWheel(center, new Avalonia.Vector(0, -5), Avalonia.Input.RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.Markdown.Offset.Y > 0, "the wheel scrolled the preview");

            // Ctrl+Shift+V again (from the preview) goes back to the source; a second preview reuses the tab.
            await workbench.OpenPreviewAsync();
            Assert.Same(source, workbench.Editors.ActiveTab);
            await workbench.OpenPreviewAsync();
            Assert.Same(preview, workbench.Editors.ActiveTab);
            Assert.Single(workbench.Editors.Tabs.OfType<PreviewTabViewModel>());
        }
        finally
        {
            window.Close();
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>An SVG file previews as a drawing; a broken one says why instead of failing.</summary>
    [AvaloniaFact]
    public async Task Svg_preview_draws_the_file_and_explains_broken_ones()
    {
        var folder = Directory.CreateTempSubdirectory("coder-svg-preview-").FullName;
        var file = Path.Combine(folder, "logo.svg");
        await File.WriteAllTextAsync(file, """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 120" width="240" height="240">
              <defs><linearGradient id="g" x1="0" y1="1" x2="1" y2="0"><stop offset="0" stop-color="#1E5BFF"/><stop offset="1" stop-color="#5AC8FA"/></linearGradient></defs>
              <rect x="10" y="10" width="100" height="100" rx="22" fill="#0B0C0E"/>
              <path d="M84 40 A30 30 0 1 0 84 80" stroke="url(#g)" stroke-width="14" fill="none" stroke-linecap="round"/>
            </svg>
            """, TestContext.Current.CancellationToken);
        var (window, workbench) = CreateWindow();
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            await workbench.OpenFileAsync(file);
            await workbench.OpenPreviewAsync();
            Dispatcher.UIThread.RunJobs();

            var preview = Assert.IsType<PreviewTabViewModel>(workbench.Editors.ActiveTab);
            Assert.Equal(PreviewKind.Svg, preview.Kind);
            var drawing = window.GetVisualDescendants().OfType<Controls.SvgView>().Single(v => v.IsEffectivelyVisible);
            Assert.NotNull(drawing.Document);
            Save(window, "svg-preview");

            // Typing broken XML into the file: the preview explains instead of failing.
            workbench.Editors.FindTab(file)!.Editor.ReplaceText("<svg");
            await WaitUntil(() => window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && (t.Text ?? string.Empty).StartsWith("This SVG can't be drawn", StringComparison.Ordinal)));
        }
        finally
        {
            window.Close();
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Clicking an extension opens its page in the editor area, with its README rendered.</summary>
    [AvaloniaFact]
    public async Task Clicking_an_extension_opens_its_page_with_the_readme()
    {
        var root = Directory.CreateTempSubdirectory("coder-store-readme-").FullName;
        using var http = new HttpClient();
        try
        {
            var package = ThemePackage(root, "acme", "ember", "Acme Ember");
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(package, TestContext.Current.CancellationToken)));
            await File.WriteAllTextAsync(Path.Combine(root, "ember-readme.md"), "# Acme Ember\n\nWarm colors for **long nights**.\n\n## Install\n\n1. Open Extensions\n2. Install it\n", TestContext.Current.CancellationToken);
            var index = Path.Combine(root, "index.json");
            await File.WriteAllTextAsync(index, $$"""{ "extensions": [ { "publisher": "acme", "name": "ember", "displayName": "Acme Ember", "version": "1.0.0", "description": "Warm dark colors", "download": "{{Path.GetFileName(package)}}", "sha256": "{{sha}}", "readme": "ember-readme.md", "downloads": 1200 } ] }""", TestContext.Current.CancellationToken);

            var store = new ExtensionStore(Path.Combine(root, "installed"), http);
            var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore(),
                extensionStore: store, extensionSources: [new CoderRegistrySource(http, index)]);
            var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
            window.Show();
            try
            {
                Button(window, "Extensions (Ctrl+Shift+X)").Command!.Execute(null);
                await WaitUntil(() => workbench.Extensions.Results.Count == 1, () => workbench.Extensions.Message);

                workbench.Extensions.Results[0].ShowDetails.Execute(null);
                Dispatcher.UIThread.RunJobs();
                var tab = Assert.IsType<ExtensionDetailsTabViewModel>(workbench.Editors.ActiveTab);
                Assert.Equal("Extension: Acme Ember", tab.Title);
                await WaitUntil(() => !tab.IsLoading);
                Dispatcher.UIThread.RunJobs();

                var view = window.GetVisualDescendants().OfType<ExtensionDetailsView>().Single();
                Assert.True(view.IsEffectivelyVisible);
                Assert.Contains("long nights", view.Readme.Text, StringComparison.Ordinal);
                Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => Avalonia.Automation.AutomationProperties.GetName(b) == "Install Acme Ember");
                Save(window, "extension-details");

                // Clicking it again shows the same tab.
                workbench.Extensions.Results[0].ShowDetails.Execute(null);
                Assert.Single(workbench.Editors.Tabs.OfType<ExtensionDetailsTabViewModel>());
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
