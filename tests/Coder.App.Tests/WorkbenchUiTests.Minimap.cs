using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Editor;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>The minimap shows the code; clicking it jumps there, dragging its slider scrolls with the pointer, and it can be turned off.</summary>
    [AvaloniaFact]
    public async Task Minimap_shows_the_code_and_scrolls_by_click_and_drag()
    {
        var (window, workbench) = CreateWindow();
        window.Show();
        await workbench.OpenFileAsync(Path.Combine(RepositoryRoot, "src", "Coder.App", "Editor", "EditorView.Rendering.cs"));
        Dispatcher.UIThread.RunJobs();
        var view = window.GetVisualDescendants().OfType<EditorView>().Single();
        Assert.True(view.ShowMinimap);
        Assert.False(view.MinimapRenderCharacters); // one bar per line unless asked for
        Save(window, "minimap");
        workbench.MinimapRendersCharacters = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.MinimapRenderCharacters);
        Save(window, "minimap-characters");

        // Click low on the map: the editor jumps down there.
        Point At(double x, double y) => view.TranslatePoint(new Point(x, y), window)!.Value;
        var mapX = view.Bounds.Width - EditorView.MinimapWidth / 2;
        window.MouseDown(At(mapX, view.Bounds.Height * 0.6), MouseButton.Left);
        window.MouseUp(At(mapX, view.Bounds.Height * 0.6), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var afterClick = view.Offset.Y;
        Assert.True(afterClick > 0, "clicking the minimap scrolls");
        var scroller = window.GetVisualDescendants().OfType<CodeEditor>().Single();
        Assert.Equal(afterClick, scroller.Offset.Y, 1); // the scroll bar follows the minimap

        // Drag the slider back up to the top.
        var sliderY = view.Bounds.Height * 0.6;
        window.MouseDown(At(mapX, sliderY), MouseButton.Left);
        window.MouseMove(At(mapX, sliderY - 2000));
        window.MouseUp(At(mapX, sliderY - 2000), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, view.Offset.Y);
        Assert.Equal(0, scroller.Offset.Y, 1);
        Assert.Null(workbench.Editors.ActiveEditor!.Editor.Selection.IsEmpty ? null : "selected"); // the map never selects text

        workbench.IsMinimapVisible = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.ShowMinimap);
    }
}
