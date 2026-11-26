using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Coder.App.Workbench;
using Coder.Core.Files;
using Coder.Core.Git;
using Coder.Presentation.Editors;
using Coder.Presentation.SourceControl;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// Source Control on a real temporary repository: changes are listed and colored in the explorer,
    /// staging moves a file between groups, saving a file refreshes the list, and committing with
    /// nothing staged offers to stage everything (the test dialog accepts) and clears the message.
    /// </summary>
    /// <summary>
    /// History on a repository with a merged branch: the GRAPH section lists the commits with a lane for
    /// the branch, a commit expands to its files and a file opens that commit's diff. Blame names the
    /// caret line's commit at its end and in the status bar, and View File History picks from the file's commits.
    /// </summary>
    [AvaloniaFact]
    public async Task History_graph_blame_and_file_history()
    {
        if (!GitRepository.IsGitInstalled)
        {
            Assert.Skip("git is not installed.");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("coder-history-").FullName;
        var file = Path.Combine(folder, "app.txt");
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.email", "test@example.com");
        Git(folder, "config", "user.name", "Test");
        Git(folder, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(file, "one\ntwo\n");
        Git(folder, "add", "-A");
        Git(folder, "commit", "-q", "-m", "Start");
        Git(folder, "checkout", "-q", "-b", "feature");
        await File.WriteAllTextAsync(Path.Combine(folder, "feature.txt"), "f\n");
        Git(folder, "add", "-A");
        Git(folder, "commit", "-q", "-m", "Add the feature");
        Git(folder, "checkout", "-q", "main");
        await File.WriteAllTextAsync(file, "one\nTWO\n");
        Git(folder, "commit", "-q", "-am", "Shout two");
        Git(folder, "merge", "-q", "--no-ff", "-m", "Merge feature", "feature");

        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore());
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            var history = workbench.SourceControl.History;
            await WaitUntil(() => history.Rows.Count == 4, () => $"rows: {history.Rows.Count}, message: {history.Message}");

            var commits = history.Rows.OfType<HistoryCommitRow>().ToList();
            // Children before parents; the order of the two sides of the merge is git's choice.
            Assert.Equal("Merge feature", commits[0].Commit.Subject);
            Assert.Equal("Start", commits[3].Commit.Subject);
            Assert.Equal(["Add the feature", "Shout two"], commits.Skip(1).Take(2).Select(c => c.Commit.Subject).Order());
            Assert.True(commits[0].IsHead);
            Assert.Contains("HEAD -> main", commits[0].Commit.Refs);
            Assert.Equal(2, commits[0].Graph.Width); // the merge opens a second lane for the branch
            Assert.Equal(0, commits[3].Graph.Lane);
            var featureCommit = commits.Single(c => c.Commit.Subject == "Add the feature");
            Assert.Equal(1, featureCommit.Graph.Lane);

            // Expand the branch's commit, open its file: a diff of that commit's change.
            await history.ToggleAsync(featureCommit);
            var fileRow = Assert.Single(history.Rows.OfType<HistoryFileRow>());
            Assert.Equal(("feature.txt", GitChangeKind.Added), (fileRow.Change.RelativePath, fileRow.Change.Kind));
            workbench.ShowSideBarView(SideBarView.SourceControl);
            Save(window, "history-graph");
            await history.OpenAsync(fileRow);
            var diff = Assert.IsType<DiffTabViewModel>(workbench.Editors.ActiveTab);
            Assert.Equal(featureCommit.Commit.ShortHash, diff.ModifiedLabel);
            Assert.Equal(["f"], diff.ModifiedLines.Where(l => l.Length > 0));

            // Blame: line 2 was changed by "Shout two", by the user (so "You").
            await workbench.OpenFileAsync(file);
            var tab = workbench.Editors.ActiveEditor!;
            tab.Editor.SetCaret(tab.Document.GetLineStart(1));
            Dispatcher.UIThread.RunJobs(); // the editor was hidden behind the diff until now: lay it out
            var view = window.GetVisualDescendants().OfType<Editor.EditorView>().Single();
            var blame = workbench.SourceControl.Blame;
            await WaitUntil(() => view.ShownBlame is not null, () => $"blame: {view.ShownBlame}, status: {blame.StatusText}");
            Assert.Equal("You, just now • Shout two", view.ShownBlame);
            Assert.Equal("You, just now", blame.StatusText);
            Assert.Contains("Shout two", blame.StatusDetail, StringComparison.Ordinal);
            Save(window, "blame");

            // A new, unsaved line is not committed yet.
            tab.Editor.SetCaret(tab.Document.Length);
            tab.Editor.TypeText("three");
            await WaitUntil(() => view.ShownBlame == "You • Uncommitted changes", () => $"blame: {view.ShownBlame}");

            // Toggling hides it.
            await workbench.Commands.Get(Presentation.Commands.CommandIds.GitToggleBlame).Execute();
            Assert.Null(view.ShownBlame);

            // File history: the two commits that changed app.txt; picking one opens its change.
            await workbench.Commands.Get(Presentation.Commands.CommandIds.GitFileHistory).Execute();
            Assert.True(workbench.Palette.IsOpen);
            Assert.Equal(["Shout two", "Start"], workbench.Palette.Items.Select(i => i.Label));
            await workbench.Palette.AcceptAsync(workbench.Palette.Items[0]);
            var fileDiff = Assert.IsType<DiffTabViewModel>(workbench.Editors.ActiveTab);
            Assert.Equal(["one", "two"], fileDiff.OriginalLines.Where(l => l.Length > 0));
            Assert.Equal(["one", "TWO"], fileDiff.ModifiedLines.Where(l => l.Length > 0));
        }
        finally
        {
            window.Close();
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }
    }

    /// <summary>
    /// A real merge conflict: the file is listed under Merge Changes and the commit box holds git's merge
    /// message. In the editor the block is tinted and has Accept buttons; accepting resolves it in one
    /// undo step. Staging a file that still has markers asks first; once resolved, committing finishes the merge.
    /// </summary>
    [AvaloniaFact]
    public async Task Merge_conflicts_are_resolved_in_the_editor()
    {
        if (!GitRepository.IsGitInstalled)
        {
            Assert.Skip("git is not installed.");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("coder-merge-").FullName;
        var file = Path.Combine(folder, "app.txt");
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.email", "test@example.com");
        Git(folder, "config", "user.name", "Test");
        await File.WriteAllTextAsync(file, "top\nvalue = 1\nbottom\n");
        Git(folder, "add", "-A");
        Git(folder, "commit", "-q", "-m", "initial");
        Git(folder, "checkout", "-q", "-b", "feature");
        await File.WriteAllTextAsync(file, "top\nvalue = 3\nbottom\n");
        Git(folder, "commit", "-q", "-am", "three");
        Git(folder, "checkout", "-q", "main");
        await File.WriteAllTextAsync(file, "top\nvalue = 2\nbottom\n");
        Git(folder, "commit", "-q", "-am", "two");
        Git(folder, "merge", "feature"); // fails with a conflict, which is what this test needs

        var dialogs = new NoDialogs();
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), dialogs, new MemorySettingsStore());
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            var scm = workbench.SourceControl;
            await WaitUntil(() => scm.Status?.Conflicts.Count == 1, () => $"conflicts: {scm.Status?.Conflicts.Count}, message: {scm.Message}");
            Assert.True(scm.IsMerging);
            Assert.StartsWith("Merge branch 'feature'", scm.CommitMessage, StringComparison.Ordinal);
            Assert.Equal(["Merge Changes", "app.txt", "Changes"], scm.Rows.Select(Label));

            // Clicking the conflicted file opens it; the block shows its actions.
            await scm.OpenChangesAsync(scm.Rows.OfType<SourceControlFileRow>().Single());
            var tab = workbench.Editors.ActiveEditor!;
            Assert.Single(tab.Conflicts);
            var actions = window.GetVisualDescendants().OfType<Editor.ConflictActions>().Single();
            await WaitUntil(() => actions.VisibleCount == 1, () => $"visible actions: {actions.VisibleCount}");
            Save(window, "merge-conflict");

            // Staging with the markers still in asks first (the test dialog says yes, so undo that).
            await scm.StageAsync(scm.Rows.OfType<SourceControlFileRow>().Single());
            Assert.Contains("\"app.txt\" still has merge conflicts.", dialogs.Asked);
            await scm.UnstageAsync(scm.Rows.OfType<SourceControlFileRow>().Single());

            // Accept Both: both sides stay, the markers go, and one undo brings the conflict back.
            var acceptBoth = actions.GetVisualDescendants().OfType<Controls.FlatButton>()
                .First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Accept Both Changes");
            acceptBoth.Command!.Execute(null);
            Assert.Equal("top\nvalue = 2\nvalue = 3\nbottom\n", tab.Document.GetText());
            await WaitUntil(() => actions.VisibleCount == 0, () => $"visible actions: {actions.VisibleCount}");
            tab.Document.Undo();
            Assert.Single(tab.Conflicts);

            // The palette command resolves all of them.
            await workbench.Commands.Get(Presentation.Commands.CommandIds.MergeAcceptAllIncoming).Execute();
            Assert.Equal("top\nvalue = 3\nbottom\n", tab.Document.GetText());

            // Save, stage (no question now) and commit: the merge is done.
            await workbench.Commands.Get(Presentation.Commands.CommandIds.Save).Execute();
            var asked = dialogs.Asked.Count;
            await scm.StageAllAsync();
            Assert.Equal(asked, dialogs.Asked.Count);
            await scm.CommitAsync();
            Assert.Equal(string.Empty, scm.Message);
            Assert.False(scm.IsMerging);
            Assert.Equal(2, Git(folder, "log", "-1", "--format=%P").Trim().Split(' ').Length); // a merge commit
        }
        finally
        {
            window.Close();
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }
    }

    /// <summary>
    /// Open files follow their files on disk: discarding in Source Control updates the open tab at once,
    /// a write by another program shows up too, and a tab with unsaved changes keeps them.
    /// </summary>
    [AvaloniaFact]
    public async Task Open_files_reload_when_they_change_on_disk()
    {
        if (!GitRepository.IsGitInstalled)
        {
            Assert.Skip("git is not installed.");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("coder-reload-").FullName;
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.email", "test@example.com");
        Git(folder, "config", "user.name", "Test");
        var app = Path.Combine(folder, "app.cs");
        var notes = Path.Combine(folder, "notes.md");
        await File.WriteAllTextAsync(app, "class App {}\n");
        await File.WriteAllTextAsync(notes, "# Notes\n");
        Git(folder, "add", "-A");
        Git(folder, "commit", "-q", "-m", "initial");

        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore());
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            var scm = workbench.SourceControl;

            // Edit and save, then discard the change in Source Control: the open tab shows the committed text.
            await workbench.OpenFileAsync(app);
            var tab = workbench.Editors.ActiveEditor!;
            tab.Editor.TypeText("// changed\n");
            await workbench.Commands.Get(Presentation.Commands.CommandIds.Save).Execute();
            await WaitUntil(() => scm.ChangeCount == 1, () => $"changes: {scm.ChangeCount}");
            await scm.DiscardAsync(scm.Rows.OfType<SourceControlFileRow>().Single());
            await WaitUntil(() => tab.Document.GetText() == "class App {}\n", () => $"tab text: {tab.Document.GetText()}");
            Assert.False(tab.IsDirty);
            Assert.True(tab.Document.CanUndo, "the reload is an undo step");

            // Another program writes the file.
            await File.WriteAllTextAsync(app, "class App { int x; }\r\n");
            await WaitUntil(() => tab.Document.GetText() == "class App { int x; }\n", () => $"tab text: {tab.Document.GetText()}");
            Assert.False(tab.IsDirty);

            // Unsaved work is never replaced.
            await workbench.OpenFileAsync(notes);
            var dirty = workbench.Editors.ActiveEditor!;
            dirty.Editor.TypeText("mine ");
            await File.WriteAllTextAsync(notes, "# Theirs\n");
            await Task.Delay(500);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("mine # Notes\n", dirty.Document.GetText());
            Assert.True(dirty.IsDirty);
        }
        finally
        {
            window.Close();
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }
    }

    [AvaloniaFact]
    public async Task Source_control_lists_stages_and_commits_changes()
    {
        if (!GitRepository.IsGitInstalled)
        {
            Assert.Skip("git is not installed.");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("coder-scm-").FullName;
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.email", "test@example.com");
        Git(folder, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(folder, "app.cs"), "class App {}\n");
        Git(folder, "add", "-A");
        Git(folder, "commit", "-q", "-m", "initial");
        await File.WriteAllTextAsync(Path.Combine(folder, "app.cs"), "class App { }\n");
        Directory.CreateDirectory(Path.Combine(folder, "docs"));
        await File.WriteAllTextAsync(Path.Combine(folder, "docs", "notes.md"), "# Notes\n");

        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore());
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            var scm = workbench.SourceControl;
            await WaitUntil(() => scm.ChangeCount == 2, () => $"changes: {scm.ChangeCount}, message: {scm.Message}");

            Assert.Equal("main", scm.BranchText);
            Assert.Equal(["Changes", "app.cs", "notes.md"], scm.Rows.Select(Label));
            var appRow = workbench.Explorer.Rows.Single(r => r.Name == "app.cs");
            Assert.Equal(GitChangeKind.Modified, appRow.GitChange);
            Assert.True(workbench.Explorer.Rows.Single(r => r.Name == "docs").ContainsGitChanges);

            await scm.StageAsync(scm.Rows.OfType<SourceControlFileRow>().Single(r => r.Change.FileName == "app.cs"));
            Assert.Equal(["Staged Changes", "app.cs", "Changes", "notes.md"], scm.Rows.Select(Label));

            workbench.ShowSideBarView(SideBarView.SourceControl);
            Save(window, "source-control");

            // Editing and saving a file shows up without a manual refresh.
            await workbench.OpenFileAsync(Path.Combine(folder, "docs", "notes.md"));
            workbench.Editors.ActiveEditor!.Editor.TypeText("more ");
            await workbench.Commands.Get(Presentation.Commands.CommandIds.Save).Execute();
            await WaitUntil(() => scm.Rows.OfType<SourceControlFileRow>().Any(r => r.Group == SourceControlGroup.Staged), () => "staged group vanished");

            // Commit: the staged file only (the untracked file stays a change).
            scm.CommitMessage = "Update app";
            await scm.CommitAsync();
            Assert.Equal(string.Empty, scm.Message);
            Assert.Equal(string.Empty, scm.CommitMessage);
            Assert.Equal("Update app", Git(folder, "log", "-1", "--format=%s").Trim());
            Assert.Equal(["Changes", "notes.md"], scm.Rows.Select(Label));

            // Nothing staged: committing offers to stage all first.
            scm.CommitMessage = "Add notes";
            await scm.CommitAsync();
            Assert.Equal("Add notes", Git(folder, "log", "-1", "--format=%s").Trim());
            Assert.Equal(0, scm.ChangeCount);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(workbench.Explorer.Rows.Single(r => r.Name == "app.cs").GitChange);
        }
        finally
        {
            window.Close();
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }
    }

    /// <summary>
    /// Diffs and gutter markers on a real repository: clicking a change opens a side-by-side diff tab
    /// (index ↔ working tree), and an open file shows which of its lines differ from the staged
    /// version, updating as the user types.
    /// </summary>
    [AvaloniaFact]
    public async Task Changes_open_as_diffs_and_show_in_the_gutter()
    {
        if (!GitRepository.IsGitInstalled)
        {
            Assert.Skip("git is not installed.");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("coder-diff-").FullName;
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.email", "test@example.com");
        Git(folder, "config", "user.name", "Test");
        var file = Path.Combine(folder, "app.cs");
        await File.WriteAllTextAsync(file, "using System;\n\nclass App\n{\n    int count;\n    string name;\n    void Run() { }\n}\n");
        Git(folder, "add", "-A");
        Git(folder, "commit", "-q", "-m", "initial");
        await File.WriteAllTextAsync(file, "using System;\nusing System.IO;\n\nclass App\n{\n    long count;\n    void Run() { }\n}\n");

        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore());
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            var scm = workbench.SourceControl;
            await WaitUntil(() => scm.ChangeCount == 1, () => $"changes: {scm.ChangeCount}");

            // Diff tab: the added using, count's type change, and the deleted field.
            await scm.OpenChangesAsync(scm.Rows.OfType<SourceControlFileRow>().Single());
            var diff = Assert.IsType<DiffTabViewModel>(workbench.Editors.ActiveTab);
            Assert.Null(workbench.Editors.ActiveEditor);
            Assert.Equal("app.cs (Working Tree)", diff.Title);
            Assert.Equal(
                [Core.Diff.DiffHunkKind.Added, Core.Diff.DiffHunkKind.Modified],
                diff.Hunks.Select(h => h.Kind));
            Dispatcher.UIThread.RunJobs();
            Save(window, "diff");

            // Gutter: the open file is compared with its staged version, and follows typing.
            await workbench.OpenFileAsync(file);
            var tab = workbench.Editors.ActiveEditor!;
            await WaitUntil(() => tab.LineChanges.Count == 2, () => $"hunks: {string.Join(", ", tab.LineChanges)}");
            tab.Editor.SetCaret(tab.Document.GetLineEnd(7));
            tab.Editor.InsertNewLine();
            tab.Editor.TypeText("// added at the end");
            await WaitUntil(() => tab.LineChanges.Count == 3 && tab.LineChanges[^1].Kind == Core.Diff.DiffHunkKind.Added,
                () => $"hunks: {string.Join(", ", tab.LineChanges)}");
            Save(window, "gutter-changes");

            // Ctrl+W closes the diff like any tab.
            workbench.Editors.ActiveTab = diff;
            await workbench.Commands.Get(Presentation.Commands.CommandIds.CloseEditor).Execute();
            Assert.DoesNotContain(diff, workbench.Editors.Tabs);
        }
        finally
        {
            window.Close();
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }
    }

    /// <summary>
    /// Branches and sync against a bare repository standing in for the remote: a new branch offers
    /// "Publish Branch", someone else's push shows as "1↓", Sync pulls it in, and the branch picker
    /// creates and switches branches.
    /// </summary>
    [AvaloniaFact]
    public async Task Branches_publish_sync_and_switch()
    {
        if (!GitRepository.IsGitInstalled)
        {
            Assert.Skip("git is not installed.");
            return;
        }

        var root = Directory.CreateTempSubdirectory("coder-sync-").FullName;
        var remote = Path.Combine(root, "remote.git");
        var mine = Path.Combine(root, "mine");
        var theirs = Path.Combine(root, "theirs");
        Git(root, "init", "-q", "--bare", "-b", "main", remote);
        Git(root, "clone", "-q", remote, mine);
        foreach (var clone in new[] { mine })
        {
            Git(clone, "config", "user.email", "test@example.com");
            Git(clone, "config", "user.name", "Test");
        }

        await File.WriteAllTextAsync(Path.Combine(mine, "a.txt"), "1\n");
        Git(mine, "add", "-A");
        Git(mine, "commit", "-q", "-m", "first");

        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore());
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        try
        {
            workbench.OpenFolder(mine);
            window.Show();
            var scm = workbench.SourceControl;
            await WaitUntil(() => scm.SyncText == "Publish Branch", () => $"sync: [{scm.SyncText}], remotes: {scm.HasRemotes}");
            Assert.Equal("Publish Branch", scm.PrimaryActionText); // the Source Control button, not the status bar

            await scm.SyncAsync(); // publishes main
            Assert.Equal(string.Empty, scm.Message);
            Assert.Equal("0↓ 0↑", scm.SyncText);

            // Someone else pushes a commit.
            Git(root, "clone", "-q", remote, theirs);
            Git(theirs, "config", "user.email", "them@example.com");
            Git(theirs, "config", "user.name", "Them");
            await File.WriteAllTextAsync(Path.Combine(theirs, "b.txt"), "theirs\n");
            Git(theirs, "add", "-A");
            Git(theirs, "commit", "-q", "-m", "their change");
            Git(theirs, "push", "-q");

            await scm.FetchAsync();
            Assert.Equal("1↓ 0↑", scm.SyncText);
            Assert.Equal("Sync Changes 1↓ 0↑", scm.PrimaryActionText);
            Dispatcher.UIThread.RunJobs();
            Save(window, "git-sync");
            await scm.SyncAsync();
            Assert.Equal("0↓ 0↑", scm.SyncText);
            Assert.True(File.Exists(Path.Combine(mine, "b.txt")));

            // Branch picker: typing a new name offers to create it (spaces become dashes).
            await workbench.ShowBranchPickerAsync();
            var palette = workbench.Palette;
            Assert.True(palette.IsOpen);
            Assert.Contains(palette.Items, item => item.Label == "main" && item.Hint == "current");
            Assert.Contains(palette.Items, item => item.Label == "origin/main");
            palette.Query = "login page";
            Dispatcher.UIThread.RunJobs();
            Save(window, "branch-picker");
            Assert.Equal("+ Create new branch \"login-page\"", palette.Items[0].Label);
            await palette.AcceptAsync();
            Assert.Equal("login-page", scm.BranchText);
            Assert.Equal("Publish Branch", scm.SyncText);

            // Switching back through the picker.
            await workbench.ShowBranchPickerAsync();
            palette.Query = "main";
            await palette.AcceptAsync(palette.Items.First(item => item.Label == "main"));
            Assert.Equal("main", scm.BranchText);
        }
        finally
        {
            window.Close();
            await workbench.DisposeAsync();
            DeleteEventually(root);
        }
    }

    /// <summary>
    /// Clicking a gutter change marker opens the change; Stage Change stages only that change, and
    /// Revert Change puts the other one back to its staged text in the editor.
    /// </summary>
    [AvaloniaFact]
    public async Task Gutter_changes_stage_and_revert_one_at_a_time()
    {
        if (!GitRepository.IsGitInstalled)
        {
            Assert.Skip("git is not installed.");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("coder-hunks-").FullName;
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.email", "test@example.com");
        Git(folder, "config", "user.name", "Test");
        var file = Path.Combine(folder, "list.txt");
        await File.WriteAllTextAsync(file, "alpha\nbeta\ngamma\ndelta\nepsilon\n");
        Git(folder, "add", "-A");
        Git(folder, "commit", "-q", "-m", "initial");
        await File.WriteAllTextAsync(file, "alpha\nBETA\ngamma\ndelta\nepsilon\nzeta\n");

        var (window, workbench) = CreateWindow();
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            await workbench.OpenFileAsync(file);
            var tab = workbench.Editors.ActiveEditor!;
            await WaitUntil(() => tab.LineChanges.Count == 2, () => $"changes: {tab.LineChanges.Count}");
            var view = window.GetVisualDescendants().OfType<Editor.EditorView>().Single();
            var peek = window.GetVisualDescendants().OfType<Editor.ChangePeek>().Single();

            // Click the marker of "beta → BETA" and stage just that change.
            Click(window, view, view.ChangeMarkerCenter(1));
            Assert.True(peek.IsOpen);
            Save(window, "change-peek");
            await PressAsync(window, "Stage Change");
            await WaitUntil(() => tab.LineChanges.Count == 1, () => $"changes: {tab.LineChanges.Count}");
            Assert.Equal("alpha\nBETA\ngamma\ndelta\nepsilon\n", Git(folder, "show", ":list.txt").ReplaceLineEndings("\n"));

            // Click the marker of the added "zeta" and revert it: gone from the editor, one undo step.
            Click(window, view, view.ChangeMarkerCenter(5));
            await PressAsync(window, "Revert Change");
            Assert.Equal("alpha\nBETA\ngamma\ndelta\nepsilon\n", tab.Document.GetText());
            await WaitUntil(() => tab.LineChanges.Count == 0, () => $"changes: {tab.LineChanges.Count}");
            tab.Editor.Undo();
            Assert.Equal("alpha\nBETA\ngamma\ndelta\nepsilon\nzeta\n", tab.Document.GetText());
        }
        finally
        {
            window.Close();
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }
    }

    private static void Click(MainWindow window, Visual target, Avalonia.Point point)
    {
        var at = target.TranslatePoint(point, window)!.Value;
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task PressAsync(MainWindow window, string name)
    {
        Button(window, name).Command!.Execute(null);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
    }

    private static string Label(SourceControlRow row) => row switch
    {
        SourceControlGroupRow group => group.Title,
        SourceControlFileRow file => file.Change.FileName,
        _ => "?",
    };

    private static string Git(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
