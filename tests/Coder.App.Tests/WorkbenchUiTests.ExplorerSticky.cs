using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Coder.App.Theme;
using Coder.App.Workbench;
using Coder.Presentation.Explorer;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// Sticky scroll: the folders the top row sits in stay pinned (at most five), slide up as their last row
    /// passes, follow the mouse wheel and the keyboard, and a click scrolls back to the folder without the
    /// pinned rows ever taking the keyboard focus.
    /// </summary>
    [AvaloniaFact]
    public async Task Explorer_sticky_scroll_pins_the_folders_above_the_top_row()
    {
        await WithExplorerAsync(async (window, workbench, folder) =>
        {
            var explorer = workbench.Explorer;
            Directory.CreateDirectory(Path.Combine(folder, "artifacts", "screenshots-before"));
            for (var i = 0; i < 30; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(folder, "artifacts", $"shot-{i:00}.png"), "x", TestContext.Current.CancellationToken);
            }

            var deepFolder = Path.Combine(folder, "deep", "l1", "l2", "l3", "l4", "l5", "l6");
            Directory.CreateDirectory(deepFolder);
            for (var i = 0; i < 50; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(deepFolder, $"file-{i:00}.ts"), "x", TestContext.Current.CancellationToken);
            }

            explorer.Refresh();
            await Settle();
            explorer.SetExpanded(explorer.Rows.Single(r => r.Name == "artifacts"), true);
            await workbench.OpenFileAsync(Path.Combine(deepFolder, "file-00.ts")); // reveals: every folder on the way opens
            await Settle();

            var view = window.GetVisualDescendants().OfType<ExplorerView>().Single();
            var sticky = view.StickyRows;
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => AutomationProperties.GetName(l) == "Files");
            var scroller = Assert.IsType<ScrollViewer>(list.Scroll);
            var rows = explorer.Rows;
            var row = Spacing.ListRowHeight;
            int Index(string name) => rows.IndexOf(rows.Single(r => r.Name == name));
            string[] Pinned() => sticky.Items.Select(i => i.Name).ToArray();

            async Task ScrollTo(double y)
            {
                scroller.Offset = new Vector(0, y);
                await Settle();
            }

            // At the top nothing is pinned; a few rows into artifacts, artifacts is.
            await ScrollTo(0);
            Assert.Empty(Pinned());
            Assert.False(sticky.IsVisible);
            var artifacts = Index("artifacts");
            await ScrollTo((artifacts + 6) * row);
            Assert.Equal(["artifacts"], Pinned());
            Assert.True(sticky.IsVisible);

            // The pinned row looks like a row and is reachable by screen readers, but never by Tab.
            var pinnedRow = sticky.GetVisualDescendants().OfType<Control>().Single(c => AutomationProperties.GetName(c) == "artifacts");
            Assert.False(pinnedRow.Focusable);
            Assert.Equal(row, pinnedRow.Bounds.Height);

            // Its last row scrolling past pushes it up, out of the way.
            var lastShot = Index("shot-29.png");
            var pushed = ExplorerStickyRows.Compute(rows, (lastShot * row) + 10);
            Assert.Equal(-10, Assert.Single(pushed).Top);

            // Deep in a nest of folders, only the five outermost are pinned.
            await ScrollTo((Index("file-03.ts") - 1) * row);
            Assert.Equal(["deep", "l1", "l2", "l3", "l4"], Pinned());
            Assert.Equal(ExplorerStickyRows.MaxRows * row, sticky.Bounds.Height, 1);
            Save(window, "explorer-sticky");

            // The mouse wheel over the pinned rows scrolls the tree under them.
            var before = scroller.Offset.Y;
            var over = sticky.TranslatePoint(new Point(40, row / 2), window)!.Value;
            window.MouseWheel(over, new Vector(0, -1));
            await Settle();
            Assert.True(scroller.Offset.Y > before);

            // The keyboard: going up onto a row hidden under the pinned folders scrolls it out from under them.
            await ScrollTo((artifacts + 10) * row);
            var shot = rows[artifacts + 11];
            FocusRow(window, workbench, shot);
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            await Settle();
            var up = rows[artifacts + 10];
            Assert.Same(up, explorer.SelectedItem);
            var upTop = (rows.IndexOf(up) * row) - scroller.Offset.Y;
            Assert.True(upTop >= sticky.Bounds.Height - 1, $"row at {upTop} is under the pinned rows ({sticky.Bounds.Height})");
            var focused = window.FocusManager?.GetFocusedElement() as Visual;
            Assert.Same(up, (focused as Control)?.DataContext);

            // Clicking a pinned folder scrolls back to it and selects it; the tree keeps the focus.
            await ScrollTo((artifacts + 12) * row);
            Assert.Equal(["artifacts"], Pinned());
            var center = sticky.TranslatePoint(new Point(60, row / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            await Settle();
            Assert.Equal(artifacts * row, scroller.Offset.Y, 1);
            Assert.Equal("artifacts", explorer.SelectedItem?.Name);
            Assert.Empty(Pinned());
            focused = window.FocusManager?.GetFocusedElement() as Visual;
            Assert.NotNull(focused?.FindAncestorOfType<ListBox>(includeSelf: true));
        });
    }

    /// <summary>The pinning rule on its own: ancestors of the row under each slot, never the row itself.</summary>
    [Fact]
    public void Sticky_rows_pin_ancestors_of_the_row_under_each_slot()
    {
        var rows = new List<ExplorerItemViewModel>();
        void Add(string name, int depth, bool folder) =>
            rows.Add(new ExplorerItemViewModel(new Core.Files.FileEntry("/" + name, name, folder), depth));
        Add("a", 0, true);
        Add("b", 1, true);
        Add("b1", 2, false);
        Add("b2", 2, false);
        Add("a1", 1, false);
        Add("z", 0, false);
        var h = Spacing.ListRowHeight;

        Assert.Empty(ExplorerStickyRows.Compute(rows, 0)); // "a" itself is at the top
        Assert.Equal(["a", "b"], ExplorerStickyRows.Compute(rows, 2 * h).Select(p => p.Item.Name)); // b1 at the top
        Assert.Equal(["a", "b"], ExplorerStickyRows.Compute(rows, 1 * h).Select(p => p.Item.Name)); // b scrolled under a's pinned row pins too
        Assert.Equal(["a"], ExplorerStickyRows.Compute(rows, 3 * h).Select(p => p.Item.Name)); // b2 ends b: a1 shows below a
        Assert.Equal(["a"], ExplorerStickyRows.Compute(rows, 2 * h, max: 1).Select(p => p.Item.Name));
        Assert.Empty(ExplorerStickyRows.Compute(rows, 5 * h)); // z is at the top level
    }
}
