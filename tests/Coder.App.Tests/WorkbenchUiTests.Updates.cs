using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Controls;
using Coder.App.Workbench;
using Coder.Core.Files;
using Coder.Presentation.Updates;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>A found update downloads in the background, then the status bar offers "Restart to Update".</summary>
    [AvaloniaFact]
    public async Task A_downloaded_update_offers_restart_and_applies_after_confirming()
    {
        var service = new FakeUpdates { Available = "0.2.0" };
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore(), updates: service);
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        window.Show();
        var pill = window.GetVisualDescendants().OfType<FlatButton>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Restart to Update");
        Assert.False(pill.IsVisible);

        await workbench.Updates.CheckNowAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(UpdateState.Ready, workbench.Updates.State);
        Assert.Equal("Restart to update to 0.2.0", workbench.Updates.StatusText);
        Assert.True(pill.IsVisible);
        Save(window, "update-ready");

        // Cancelling the restart (unsaved files) keeps everything as it was.
        workbench.Updates.ConfirmRestart = () => Task.FromResult(false);
        await workbench.Updates.RestartToUpdateAsync();
        Assert.Equal(0, service.Restarts);

        workbench.Updates.ConfirmRestart = () => Task.FromResult(true);
        await workbench.Updates.RestartToUpdateAsync();
        Assert.Equal(1, service.Restarts);
        workbench.Updates.OnExit();
        Assert.Equal(0, service.ExitApplies); // the restart already applies it
    }

    /// <summary>Closing with a downloaded update installs it after exit; offline background checks stay quiet.</summary>
    [Fact]
    public async Task Updates_install_on_exit_and_background_failures_stay_quiet()
    {
        var service = new FakeUpdates { Available = "0.3.0" };
        using var updates = new UpdatesViewModel(service);
        await updates.CheckNowAsync();
        updates.OnExit();
        Assert.Equal(1, service.ExitApplies);

        var offline = new FakeUpdates { Fail = true };
        using var quiet = new UpdatesViewModel(offline);
        await quiet.CheckNowAsync();
        Assert.Equal(UpdateState.Failed, quiet.State); // asked for: says why
        Assert.StartsWith("Couldn't check for updates", quiet.StatusText, StringComparison.Ordinal);

        var latest = new FakeUpdates();
        using var upToDate = new UpdatesViewModel(latest);
        await upToDate.CheckNowAsync();
        Assert.Equal(UpdateState.UpToDate, upToDate.State);

        using var source = new UpdatesViewModel(new NoUpdates());
        Assert.Equal(UpdateState.NotInstalled, source.State);
    }

    /// <summary>
    /// The title bar's cog opens the Manage menu (settings moved there from the activity bar), the side
    /// bar toggles mirror when the side bar moves right, and the activity bar lying across the side bar is slim.
    /// </summary>
    [AvaloniaFact]
    public void Title_bar_cog_side_toggles_and_slim_activity_bar()
    {
        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<FlatButton>(), b => Avalonia.Automation.AutomationProperties.GetName(b) == "Settings (Ctrl+,)");
        Button(window, "Manage"); // the cog

        FlatButton Toggle(string name) => window.GetVisualDescendants().OfType<FlatButton>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == name);
        double X(Visual v) => v.TranslatePoint(default, window)!.Value.X;
        Assert.True(X(Toggle("Toggle Primary Side Bar (Ctrl+B)")) < X(Toggle("Toggle Secondary Side Bar (Ctrl+Alt+B)")));
        workbench.IsSideBarOnRight = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(X(Toggle("Toggle Primary Side Bar (Ctrl+B)")) > X(Toggle("Toggle Secondary Side Bar (Ctrl+Alt+B)")), "the primary toggle is on the right now");

        Toggle("Toggle Primary Side Bar (Ctrl+B)").Command!.Execute(null);
        Assert.False(workbench.IsSideBarVisible);
        workbench.IsSideBarVisible = true;

        workbench.ActivityBarLocation = "bottom";
        Dispatcher.UIThread.RunJobs();
        var bar = window.GetVisualDescendants().OfType<ActivityBar>().Single();
        Assert.Equal(32, bar.Bounds.Height, 1);
        Save(window, "activity-bar-bottom-slim");
    }

    private sealed class FakeUpdates : IUpdateService
    {
        public string? Available { get; init; }

        public bool Fail { get; init; }

        public int Restarts { get; private set; }

        public int ExitApplies { get; private set; }

        public bool IsInstalled => true;

        public string CurrentVersion => "0.1.0";

        public Task<string?> CheckAsync(CancellationToken cancellationToken = default) =>
            Fail ? Task.FromException<string?>(new HttpRequestException("offline")) : Task.FromResult(Available);

        public Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken = default)
        {
            progress(100);
            return Task.CompletedTask;
        }

        public void ApplyAndRestart() => Restarts++;

        public void ApplyOnExit() => ExitApplies++;
    }
}
