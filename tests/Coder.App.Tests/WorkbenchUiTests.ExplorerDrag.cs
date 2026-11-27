using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Workbench;
using Coder.Core.Extensions;
using Coder.Presentation.Explorer;
using Coder.Presentation.Extensions;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// Go to Definition into node_modules: the folder of thousands of entries opens in one step and the
    /// file's row is shown at once (no scrolling through the list), quickly.
    /// </summary>
    [AvaloniaFact]
    public async Task Revealing_a_file_deep_in_a_huge_folder_is_instant()
    {
        await WithExplorerAsync(async (window, workbench, folder) =>
        {
            var modules = Path.Combine(folder, "node_modules");
            for (var i = 0; i < 3000; i++)
            {
                Directory.CreateDirectory(Path.Combine(modules, $"pkg{i:D4}"));
            }

            var deep = Path.Combine(modules, "pkg2500", "dist", "index.d.ts");
            Directory.CreateDirectory(Path.GetDirectoryName(deep)!);
            await File.WriteAllTextAsync(deep, "export {};\n", TestContext.Current.CancellationToken);
            workbench.Explorer.Refresh();
            Dispatcher.UIThread.RunJobs();

            var rowsChanged = 0;
            workbench.Explorer.Rows.CollectionChanged += (_, _) => rowsChanged++;
            var clock = Stopwatch.StartNew();
            Assert.True(workbench.Explorer.Reveal(deep));
            Dispatcher.UIThread.RunJobs();
            clock.Stop();

            Assert.Equal(3, rowsChanged); // node_modules, pkg2500, dist: one change each, not one per row
            Assert.True(clock.ElapsedMilliseconds < 2000, $"took {clock.ElapsedMilliseconds} ms");
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => Avalonia.Automation.AutomationProperties.GetName(l) == "Files");
            var row = list.ContainerFromItem(workbench.Explorer.SelectedItem!);
            Assert.NotNull(row); // already on screen: built, not scrolled to later
            var top = row.TranslatePoint(default, list)!.Value.Y;
            Assert.InRange(top, 0, list.Bounds.Height - row.Bounds.Height);
            Save(window, "explorer-reveal-node-modules");

            // Closing it again is one change too.
            rowsChanged = 0;
            workbench.Explorer.SetExpanded(workbench.Explorer.Rows.Single(r => r.Name == "node_modules"), false);
            Assert.Equal(1, rowsChanged);
            Assert.DoesNotContain(workbench.Explorer.Rows, r => r.Path.StartsWith(modules + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        });
    }

    /// <summary>
    /// Dragging with the mouse: a label says what will happen and the target folder lights up with its
    /// rows; over the file's own folder it says it is already there; Esc cancels; releasing on a folder moves.
    /// </summary>
    [AvaloniaFact]
    public async Task Explorer_drag_shows_where_the_file_goes_and_moves_it()
    {
        await WithExplorerAsync(async (window, workbench, folder) =>
        {
            var explorer = workbench.Explorer;
            var view = window.GetVisualDescendants().OfType<ExplorerView>().Single();
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => Avalonia.Automation.AutomationProperties.GetName(l) == "Files");
            explorer.SetExpanded(explorer.Rows.Single(r => r.Name == "src"), true);
            Dispatcher.UIThread.RunJobs();
            Point Center(ExplorerItemViewModel row)
            {
                var container = list.ContainerFromItem(row)!;
                return container.TranslatePoint(new Point(40, container.Bounds.Height / 2), window)!.Value;
            }

            var app = explorer.Rows.Single(r => r.Name == "app.ts");
            var src = explorer.Rows.Single(r => r.Name == "src");
            var start = Center(app);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Point(0, 3));
            Assert.Null(view.DragLabel); // a few pixels: still a click

            window.MouseMove(Center(src));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Move app.ts into src", view.DragLabel);
            var srcIndex = explorer.Rows.IndexOf(src);
            Assert.Equal((srcIndex, srcIndex + 2), view.DropHighlight.Range); // src and lib, shown under it
            Assert.Contains("drop-target", list.ContainerFromItem(src)!.Classes);
            Assert.Contains("drop-inside", list.ContainerFromItem(explorer.Rows.Single(r => r.Name == "lib"))!.Classes);
            Save(window, "explorer-drag-label");

            window.MouseMove(Center(app));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal($"app.ts is already in {explorer.RootName}", view.DragLabel);
            Assert.Null(view.DropHighlight.Range);

            // Esc: nothing moves, nothing stays highlighted, and the release isn't a click.
            window.MouseMove(Center(src));
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(view.DragLabel);
            Assert.Null(view.DropHighlight.Range);
            window.MouseUp(Center(src), MouseButton.Left);
            await Settle();
            Assert.True(File.Exists(Path.Combine(folder, "app.ts")));

            // Again (a moment later, so not a double-click), released on src: moved.
            await Task.Delay(700, TestContext.Current.CancellationToken);
            window.MouseDown(Center(app), MouseButton.Left);
            window.MouseMove(Center(src));
            window.MouseUp(Center(src), MouseButton.Left);
            await Settle();
            Assert.True(File.Exists(Path.Combine(folder, "src", "app.ts")));
            Assert.Null(view.DragLabel);
            Assert.Null(workbench.Editors.ActiveEditor); // releasing didn't also open the file
        });
    }

    /// <summary>Files and folders dropped from outside Coder are copied into the folder under the pointer, never overwriting.</summary>
    [AvaloniaFact]
    public async Task Files_dropped_from_outside_are_copied_in()
    {
        var outside = Directory.CreateTempSubdirectory("coder-outside-").FullName;
        try
        {
            var image = Path.Combine(outside, "logo.png");
            await File.WriteAllBytesAsync(image, [0x89, 0x50, 0x4E, 0x47], TestContext.Current.CancellationToken);
            var assets = Path.Combine(outside, "assets");
            Directory.CreateDirectory(Path.Combine(assets, "icons"));
            await File.WriteAllTextAsync(Path.Combine(assets, "icons", "a.svg"), "<svg/>", TestContext.Current.CancellationToken);

            await WithExplorerAsync(async (window, workbench, folder) =>
            {
                var view = window.GetVisualDescendants().OfType<ExplorerView>().Single();
                var src = workbench.Explorer.Rows.Single(r => r.Name == "src");
                var added = await view.DropFilesAsync([image, assets], src);
                Assert.Equal([Path.Combine(folder, "src", "logo.png"), Path.Combine(folder, "src", "assets")], added);
                Assert.True(File.Exists(Path.Combine(folder, "src", "assets", "icons", "a.svg")));
                Assert.Equal(Path.Combine(folder, "src", "assets"), workbench.Explorer.SelectedItem?.Path);
                Assert.Equal("Added 2 items to src.", workbench.StatusBar.Message);

                // The same image again: a copy beside it, the first one untouched.
                var again = await view.DropFilesAsync([image], src);
                Assert.Equal([Path.Combine(folder, "src", "logo copy.png")], again);

                // On a file: into that file's folder; on empty space: the top of the project.
                var top = await view.DropFilesAsync([image], null);
                Assert.Equal([Path.Combine(folder, "logo.png")], top);
                Assert.True(File.Exists(image), "copied, not moved");
            });
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>Open VSX searches loosely; results keep only what matches the query, best matches first.</summary>
    [Fact]
    public void Marketplace_results_keep_what_matches_best_first()
    {
        static ExtensionListing L(string name, string id, string description, long downloads) =>
            new(id, name, id.Split('.')[0], description, "1.0.0", downloads, false, null, "x", null, null, "Open VSX");
        var found = new[]
        {
            L("Mayukai Theme", "gulajavaministudio.mayukai", "A mirage colored theme", 900_000),
            L("Dracula Remastered", "someone.dracula-remastered", "Dracula, remastered", 50_000),
            L("Owlet", "a.owlet", "Night owl and dracula inspired", 10_000),
            L("Dracula Theme Official", "dracula-theme.theme-dracula", "Official Dracula Theme", 5_000_000),
            L("Dracula", "zz.dracula", "Plain", 10),
        };

        Assert.Equal(["Dracula", "Dracula Theme Official", "Dracula Remastered", "Owlet"],
            ExtensionsViewModel.Rank(found, "dracula").Select(l => l.DisplayName));
        Assert.Equal(["Dracula Theme Official"], ExtensionsViewModel.Rank(found, "dracula official").Select(l => l.DisplayName));
        Assert.Equal(found.Select(l => l.DisplayName), ExtensionsViewModel.Rank(found, "  ").Select(l => l.DisplayName)); // popular list as sent
    }
}
