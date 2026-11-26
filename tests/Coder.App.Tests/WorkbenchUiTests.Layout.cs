using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Coder.App.Editor;
using Coder.App.Workbench;
using Coder.Presentation.Editors;
using Coder.Presentation.Explorer;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>One wheel tick scrolls three lines, like VS Code (it used to scroll one).</summary>
    [AvaloniaFact]
    public async Task Mouse_wheel_scrolls_three_lines_per_tick()
    {
        var (window, workbench) = CreateWindow();
        window.Show();
        await workbench.OpenFileAsync(Path.Combine(RepositoryRoot, "src", "Coder.Core", "Text", "PieceTable.cs"));
        Dispatcher.UIThread.RunJobs();
        var view = window.GetVisualDescendants().OfType<EditorView>().Single();
        var center = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), window)!.Value;

        window.MouseWheel(center, new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();

        var lineHeight = Math.Ceiling(workbench.EditorFontSize * Theme.Typography.LineHeightFactor);
        Assert.Equal(3 * lineHeight, view.Offset.Y);

        // The scroll bar follows: its value and its thumb move with the text.
        var scrollBar = view.FindAncestorOfType<CodeEditor>()!.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        var thumb = scrollBar.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().Single();
        var thumbTop = thumb.Bounds.Y;
        for (var i = 0; i < 10; i++)
        {
            window.MouseWheel(center, new Vector(0, -1));
        }

        Dispatcher.UIThread.RunJobs();
        Assert.Equal(view.Offset.Y, scrollBar.Value);
        Assert.True(thumb.Bounds.Y > thumbTop, $"thumb stayed at {thumb.Bounds.Y}");
        window.Close();
    }

    /// <summary>
    /// Regression (reported): after clicking on the scroll bar, wheel scrolling no longer moved it.
    /// Clicks on the track page the text; the bar must keep following afterwards.
    /// </summary>
    [AvaloniaFact]
    public async Task Scroll_bar_keeps_following_after_it_was_clicked()
    {
        var (window, workbench) = CreateWindow();
        window.Show();
        await workbench.OpenFileAsync(Path.Combine(RepositoryRoot, "src", "Coder.Core", "Text", "PieceTable.cs"));
        Dispatcher.UIThread.RunJobs();
        var view = window.GetVisualDescendants().OfType<EditorView>().Single();
        var scrollBar = view.FindAncestorOfType<CodeEditor>()!.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        var thumb = scrollBar.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().Single();

        // Click the track below the thumb, then on the thumb itself.
        foreach (var target in new[] { new Point(scrollBar.Bounds.Width / 2, scrollBar.Bounds.Height - 20), thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), scrollBar)!.Value })
        {
            var at = scrollBar.TranslatePoint(target, window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        // Then drag the thumb down a little, as when scrolling with the bar.
        var beforeDrag = view.Offset.Y;
        var grab = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
        window.MouseDown(grab, MouseButton.Left);
        window.MouseMove(grab + new Point(0, 40));
        window.MouseMove(grab + new Point(0, 80));
        window.MouseUp(grab + new Point(0, 80), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.Offset.Y > beforeDrag, "dragging the thumb scrolled the text");
        Assert.True(Math.Abs(scrollBar.Value - view.Offset.Y) < 1, $"after the drag: scroll bar at {scrollBar.Value}, text at {view.Offset.Y}");

        var center = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), window)!.Value;
        var before = view.Offset.Y;
        for (var i = 0; i < 5; i++)
        {
            window.MouseWheel(center, new Vector(0, -1));
        }

        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(before, view.Offset.Y);
        Assert.True(Math.Abs(scrollBar.Value - view.Offset.Y) < 1, $"scroll bar at {scrollBar.Value}, text at {view.Offset.Y}");
        window.Close();
    }

    /// <summary>
    /// Dragging the thumb keeps it under the pointer, like any scroll bar: after each small mouse move
    /// (and after moves that arrive faster than layout), the grabbed point of the thumb is still where
    /// the pointer is, so the text moves exactly as far as the hand.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scroll_bar_thumb_stays_under_the_pointer(bool layoutBetweenMoves)
    {
        var (window, workbench) = CreateWindow();
        window.Show();
        await workbench.OpenFileAsync(Path.Combine(RepositoryRoot, "src", "Coder.Core", "Text", "PieceTable.cs"));
        Dispatcher.UIThread.RunJobs();
        var view = window.GetVisualDescendants().OfType<EditorView>().Single();
        var scrollBar = view.FindAncestorOfType<CodeEditor>()!.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        var thumb = scrollBar.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().Single();
        double ThumbTop() => thumb.TranslatePoint(default, window)!.Value.Y;

        var grab = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
        var grabOffset = grab.Y - ThumbTop();
        window.MouseDown(grab, MouseButton.Left);
        for (var step = 1; step <= 12; step++)
        {
            window.MouseMove(grab + new Point(0, step * 8));
            if (layoutBetweenMoves)
            {
                Dispatcher.UIThread.RunJobs();
                var drift = grab.Y + step * 8 - ThumbTop() - grabOffset;
                Assert.True(Math.Abs(drift) < 1.5, $"step {step}: the thumb is {drift:0.0}px away from the pointer");
            }
        }

        Dispatcher.UIThread.RunJobs();
        var finalDrift = grab.Y + 12 * 8 - ThumbTop() - grabOffset;
        Assert.True(Math.Abs(finalDrift) < 1.5, $"after the drag the thumb is {finalDrift:0.0}px away from the pointer");

        // And back up past the start: the thumb stops at the top, then follows again on the way down.
        window.MouseMove(grab + new Point(0, -400));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, view.Offset.Y);
        window.MouseMove(grab + new Point(0, 24));
        Dispatcher.UIThread.RunJobs();
        Assert.True(Math.Abs(grab.Y + 24 - ThumbTop() - grabOffset) < 1.5, "follows again after hitting the top");
        window.MouseUp(grab + new Point(0, 24), MouseButton.Left);
        Assert.True(Math.Abs(scrollBar.Value - view.Offset.Y) < 1, $"scroll bar at {scrollBar.Value}, text at {view.Offset.Y}");
        window.Close();
    }

    /// <summary>Every way the editor scrolls itself (keys, caret, tab switches) moves the scroll bar too.</summary>
    [AvaloniaFact]
    public async Task Scroll_bar_follows_every_kind_of_scrolling()
    {
        var (window, workbench) = CreateWindow();
        window.Show();
        var first = Path.Combine(RepositoryRoot, "src", "Coder.Core", "Text", "PieceTable.cs");
        var second = Path.Combine(RepositoryRoot, "src", "Coder.Core", "Editing", "EditorState.cs");
        await workbench.OpenFileAsync(first);
        Dispatcher.UIThread.RunJobs();
        var view = window.GetVisualDescendants().OfType<EditorView>().Single();
        var scrollBar = view.FindAncestorOfType<CodeEditor>()!.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        void Check(string step)
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(Math.Abs(scrollBar.Value - view.Offset.Y) < 1, $"{step}: scroll bar at {scrollBar.Value}, text at {view.Offset.Y}");
        }

        view.Focus();
        window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.Control);
        Check("Ctrl+End");
        window.KeyPressQwerty(PhysicalKey.PageUp, RawInputModifiers.None);
        Check("PageUp");
        workbench.Editors.ActiveEditor!.Editor.SetCaret(0);
        Check("caret to the top");
        window.KeyPressQwerty(PhysicalKey.PageDown, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.PageDown, RawInputModifiers.None);
        Check("PageDown");
        var scrolled = view.Offset.Y;

        await workbench.OpenFileAsync(second);
        Check("another tab");
        workbench.Editors.ActiveTab = workbench.Editors.Tabs.First();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(scrolled, view.Offset.Y);
        Check("back to the first tab");
        window.Close();
    }

    /// <summary>Lists (the explorer here) scroll by wheel and their scroll bar follows.</summary>
    [AvaloniaFact]
    public void Explorer_scroll_bar_follows_the_wheel()
    {
        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        foreach (var name in new[] { "src", "tests", "tools" })
        {
            workbench.Explorer.SetExpanded(workbench.Explorer.Rows.First(r => r.Name == name), true);
        }

        foreach (var folder in workbench.Explorer.Rows.Where(r => r.IsDirectory && r.Depth == 1).ToList())
        {
            workbench.Explorer.SetExpanded(folder, true);
        }

        Dispatcher.UIThread.RunJobs();
        var list = window.GetVisualDescendants().OfType<ListBox>().First(l => Avalonia.Automation.AutomationProperties.GetName(l) == "Files");
        var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        var scrollBar = viewer.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        var thumb = scrollBar.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().Single();
        var thumbTop = thumb.Bounds.Y;
        var center = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height / 2), window)!.Value;

        for (var i = 0; i < 5; i++)
        {
            window.MouseWheel(center, new Vector(0, -1));
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(viewer.Offset.Y > 0, "the list scrolled");
        Assert.True(Math.Abs(scrollBar.Value - viewer.Offset.Y) < 1, $"scroll bar at {scrollBar.Value}, list at {viewer.Offset.Y}");
        Assert.True(thumb.Bounds.Y > thumbTop, $"thumb stayed at {thumb.Bounds.Y} (list offset {viewer.Offset.Y}, extent {viewer.Extent.Height}, viewport {viewer.Viewport.Height})");
        window.Close();
    }

    /// <summary>
    /// Many open files overflow the tab strip: a slim scroll bar appears, the wheel scrolls the tabs
    /// sideways, and the active tab is scrolled into view when it changes.
    /// </summary>
    [AvaloniaFact]
    public async Task Editor_tabs_scroll_when_they_overflow()
    {
        var (window, workbench) = CreateWindow();
        window.Width = 900;
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        var files = Directory.GetFiles(Path.Combine(RepositoryRoot, "src", "Coder.App", "Workbench"), "*.cs").Take(20).ToList();
        foreach (var file in files)
        {
            await workbench.OpenFileAsync(file);
        }

        Dispatcher.UIThread.RunJobs();
        var strip = window.GetVisualDescendants().OfType<EditorTabStrip>().Single();
        var viewer = strip.GetVisualDescendants().OfType<ScrollViewer>().First();
        var scrollBar = viewer.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .Single(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);
        Assert.True(viewer.Extent.Width > viewer.Viewport.Width, "the tabs overflow");
        Assert.True(scrollBar.IsVisible, "the scroll bar shows");
        Assert.Equal(5, scrollBar.Bounds.Height);

        // The last opened tab is active, so the strip was scrolled to it.
        Assert.True(viewer.Offset.X > 0, "the active tab at the end is in view");
        Save(window, "tabs-overflow");

        // The wheel scrolls sideways; the scroll bar follows.
        var center = strip.TranslatePoint(new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), window)!.Value;
        var before = viewer.Offset.X;
        window.MouseWheel(center, new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewer.Offset.X < before, $"wheel up scrolled left ({before} -> {viewer.Offset.X})");
        Assert.True(Math.Abs(scrollBar.Value - viewer.Offset.X) < 1, $"scroll bar at {scrollBar.Value}, tabs at {viewer.Offset.X}");

        // Choosing the first tab brings it back into view.
        workbench.Editors.ActiveTab = workbench.Editors.Tabs[0];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, viewer.Offset.X);
        window.Close();
    }

    /// <summary>
    /// A folder opens and closes again as fast as the user clicks: the second click arrives as a
    /// double-tap, which used to be ignored until the double-click time had passed.
    /// </summary>
    [AvaloniaFact]
    public void Clicking_a_folder_twice_quickly_opens_and_closes_it()
    {
        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var folder = workbench.Explorer.Rows.Single(r => r.Name == "src");
        var row = window.GetVisualDescendants().OfType<ListBoxItem>().Single(i => ReferenceEquals(i.DataContext, folder));
        var point = row.TranslatePoint(new Point(40, row.Bounds.Height / 2), window)!.Value;

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(folder.IsExpanded);

        window.MouseDown(point, MouseButton.Left); // immediately: a double-click by the clock
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.False(folder.IsExpanded);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Images_open_in_an_image_tab()
    {
        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();

        await workbench.OpenFileAsync(Path.Combine(RepositoryRoot, "assets", "coder.png"));
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<ImageTabViewModel>(workbench.Editors.ActiveTab);
        var viewer = window.GetVisualDescendants().OfType<ImageViewer>().Single();
        Assert.True(viewer.IsEffectivelyVisible);
        Assert.Contains("512 × 512", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty), StringComparer.Ordinal.WithContains());
        Save(window, "image-viewer");
        window.Close();
    }

    /// <summary>The title bar: project switcher (recent projects) and layout toggles; the status bar keeps only the branch and counts.</summary>
    [AvaloniaFact]
    public void Title_bar_switches_projects_and_toggles_the_layout()
    {
        var store = new MemorySettingsStore();
        store.Save(new Presentation.Settings.UserSettings { RecentFolders = [RepositoryRoot, Path.GetTempPath().TrimEnd('\\')] });
        var workbench = new Presentation.Workbench.WorkbenchViewModel(new Core.Files.LocalFileSystem(), new NoDialogs(), store);
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The project name opens a dropdown (not the command palette).
        Button(window, "Recent projects").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var switcher = window.GetVisualDescendants().OfType<ProjectSwitcher>().Single();
        Assert.True(switcher.IsOpen);
        Assert.False(workbench.Palette.IsOpen);
        var list = switcher.GetLogicalDescendants().OfType<ListBox>().Single();
        var popupRoot = TopLevel.GetTopLevel(list)!;
        Assert.Equal(["Coder", "Temp", "Open Folder…"], list.Items.Cast<object>().Select(i => Avalonia.Automation.AutomationProperties.GetName(list.ContainerFromItem(i)!)));
        popupRoot.CaptureRenderedFrame()?.Save(Path.Combine(RepositoryRoot, "artifacts", "screenshots", "project-dropdown.png"));

        // Down + Enter switches to the other project.
        popupRoot.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        popupRoot.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(switcher.IsOpen);
        Assert.Equal(Path.GetTempPath().TrimEnd('\\'), workbench.Explorer.RootPath);
        workbench.OpenFolder(RepositoryRoot);

        Button(window, "Toggle Secondary Side Bar (Ctrl+Alt+B)").Command!.Execute(null);
        Button(window, "Toggle Panel (Ctrl+J)").Command!.Execute(null);
        Assert.True(workbench.IsSecondarySideBarVisible);
        Assert.True(workbench.IsPanelVisible);
        Dispatcher.UIThread.RunJobs();
        Save(window, "layout");
        Button(window, "Toggle Primary Side Bar (Ctrl+B)").Command!.Execute(null);
        Assert.False(workbench.IsSideBarVisible);

        var statusTexts = window.GetVisualDescendants().OfType<StatusBarView>().Single().GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.DoesNotContain("Coder", statusTexts); // the project name moved to the title bar
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => Avalonia.Automation.AutomationProperties.GetName(b) == "Synchronize Changes");
        window.Close();
    }
}

internal static class StringComparerExtensions
{
    /// <summary>Equality that matches when the candidate contains the expected text (for Assert.Contains on labels).</summary>
    public static IEqualityComparer<string> WithContains(this StringComparer _) => new ContainsComparer();

    private sealed class ContainsComparer : IEqualityComparer<string>
    {
        public bool Equals(string? expected, string? actual) => actual?.Contains(expected ?? string.Empty, StringComparison.Ordinal) == true;

        public int GetHashCode(string obj) => 0;
    }
}
