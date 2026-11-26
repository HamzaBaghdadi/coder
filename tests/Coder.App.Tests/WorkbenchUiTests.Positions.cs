using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>The primary side bar moves to the right (the secondary to the left), and the activity bar goes to the top, bottom, edge or away.</summary>
    [AvaloniaFact]
    public void Side_bar_and_activity_bar_positions_apply_live()
    {
        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        workbench.IsSecondarySideBarVisible = true;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var sideBar = window.GetVisualDescendants().OfType<SideBar>().Single();
        var secondary = window.GetVisualDescendants().OfType<SecondarySideBar>().Single();
        var activityBar = window.GetVisualDescendants().OfType<ActivityBar>().Single();
        double X(Visual v) => v.TranslatePoint(default, window)!.Value.X;

        Assert.True(X(activityBar) < X(sideBar) && X(sideBar) < X(secondary));

        workbench.IsSideBarOnRight = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(X(secondary) < X(sideBar), "the secondary side bar takes the left");
        Assert.True(X(sideBar) < X(activityBar), "the activity bar stays on the outer edge, now the right");
        Save(window, "layout-side-bar-right");

        workbench.ActivityBarLocation = "top";
        Dispatcher.UIThread.RunJobs();
        Assert.True(activityBar.IsHorizontal);
        Assert.Equal(X(sideBar), X(activityBar), 1);
        Assert.True(activityBar.TranslatePoint(default, window)!.Value.Y < sideBar.TranslatePoint(default, window)!.Value.Y);
        Save(window, "layout-activity-bar-top");

        workbench.ActivityBarLocation = "bottom";
        Dispatcher.UIThread.RunJobs();
        Assert.True(activityBar.TranslatePoint(default, window)!.Value.Y > sideBar.TranslatePoint(default, window)!.Value.Y);

        workbench.ActivityBarLocation = "hidden";
        Dispatcher.UIThread.RunJobs();
        Assert.Null(activityBar.Parent);

        workbench.IsSideBarOnRight = false;
        workbench.ActivityBarLocation = "side";
        Dispatcher.UIThread.RunJobs();
        Assert.True(X(activityBar) < X(sideBar));
    }

    /// <summary>Double-clicking the side bar's edge returns it to its default width (from settings).</summary>
    [AvaloniaFact]
    public void Double_clicking_the_side_bar_edge_resets_its_width()
    {
        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var sideBar = window.GetVisualDescendants().OfType<SideBar>().Single();
        var splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single(s => Avalonia.Automation.AutomationProperties.GetName(s) == "Resize Side Bar");
        var grid = (Grid)splitter.Parent!;
        grid.ColumnDefinitions[Grid.GetColumn(sideBar.Parent as Control ?? sideBar)].Width = new GridLength(420);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(420, sideBar.Bounds.Width, 1);

        splitter.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(workbench.Settings.SideBarDefaultWidth, sideBar.Bounds.Width, 1);
    }
}
