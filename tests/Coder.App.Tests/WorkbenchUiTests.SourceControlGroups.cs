using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Workbench;
using Coder.Core.Files;
using Coder.Core.Git;
using Coder.Presentation.SourceControl;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// Source Control's groups fold like VS Code's: clicking (or Enter on) a header hides its files and keeps
    /// the header with its count; folding survives refreshes; "Stage All" still stages a folded group's files.
    /// </summary>
    [AvaloniaFact]
    public async Task Source_control_groups_fold_and_unfold()
    {
        if (!GitRepository.IsGitInstalled)
        {
            Assert.Skip("git is not installed.");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("coder-scm-fold-").FullName;
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.email", "test@example.com");
        Git(folder, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(folder, "app.cs"), "class App {}\n", TestContext.Current.CancellationToken);
        Git(folder, "add", "-A");
        Git(folder, "commit", "-q", "-m", "initial");
        await File.WriteAllTextAsync(Path.Combine(folder, "app.cs"), "class App { }\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.md"), "# Notes\n", TestContext.Current.CancellationToken);

        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore());
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            var scm = workbench.SourceControl;
            await WaitUntil(() => scm.ChangeCount == 2, () => $"changes: {scm.ChangeCount}, message: {scm.Message}");
            workbench.ShowSideBarView(SideBarView.SourceControl);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["Changes", "app.cs", "notes.md"], scm.Rows.Select(Label));

            // Folding hides the files; the header keeps its count.
            scm.ToggleGroup(SourceControlGroup.Changes);
            var header = Assert.IsType<SourceControlGroupRow>(Assert.Single(scm.Rows));
            Assert.False(header.IsExpanded);
            Assert.Equal(2, header.Count);
            Dispatcher.UIThread.RunJobs();
            Save(window, "source-control-folded");

            // It stays folded when the list refreshes, and Stage All still finds the folded files.
            await scm.StageAllAsync();
            await WaitUntil(() => scm.Rows.OfType<SourceControlGroupRow>().Any(g => g.Group == SourceControlGroup.Staged));
            Assert.Equal(["Staged Changes", "app.cs", "notes.md", "Changes"], scm.Rows.Select(Label));
            Assert.False(scm.Rows.OfType<SourceControlGroupRow>().Single(g => g.Group == SourceControlGroup.Changes).IsExpanded);

            // Clicking the header unfolds it again (the view handles the click).
            var list = window.GetVisualDescendants().OfType<SourceControlView>().Single().GetVisualDescendants().OfType<ListBox>().First();
            Dispatcher.UIThread.RunJobs();
            var changesHeader = list.ContainerFromItem(scm.Rows.OfType<SourceControlGroupRow>().Single(g => g.Group == SourceControlGroup.Changes))!;
            Assert.Contains("collapsed", Avalonia.Automation.AutomationProperties.GetName(changesHeader), StringComparison.Ordinal);
            changesHeader.RaiseEvent(new Avalonia.Input.TappedEventArgs(Avalonia.Input.InputElement.TappedEvent, null!) { Source = changesHeader });
            Assert.True(scm.Rows.OfType<SourceControlGroupRow>().Single(g => g.Group == SourceControlGroup.Changes).IsExpanded);
        }
        finally
        {
            window.Close();
            await workbench.DisposeAsync();
            DeleteEventually(folder); // git keeps its objects read-only
        }
    }
}
