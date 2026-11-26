using Avalonia;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Coder.App.Controls;
using Coder.App.Terminal;
using Coder.App.Workbench;
using Coder.Core.Files;
using Coder.Presentation.Search;
using Coder.Presentation.Services;
using Coder.Presentation.Settings;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

/// <summary>
/// End-to-end UI checks on the headless platform. Each test also saves a screenshot to
/// <c>artifacts/screenshots</c> so the visual design can be reviewed without launching the app.
/// </summary>
public sealed partial class WorkbenchUiTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [AvaloniaFact]
    public void Welcome_screen_renders()
    {
        var (window, _) = CreateWindow();

        window.Show();
        window.MouseMove(new Avalonia.Point(700, 428)); // hover the first action row
        Save(window, "welcome");
    }

    [AvaloniaFact]
    public async Task Opening_a_file_shows_it_in_the_editor_and_typing_edits_it()
    {
        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        workbench.Explorer.SetExpanded(workbench.Explorer.Rows.First(row => row.Name == "src"), true);
        window.Show();

        await workbench.OpenFileAsync(Path.Combine(RepositoryRoot, "src", "Coder.Core", "Editing", "EditorState.cs"));
        Dispatcher.UIThread.RunJobs();

        var tab = Assert.IsType<Presentation.Editors.EditorTabViewModel>(Assert.Single(workbench.Editors.Tabs));
        tab.Editor.SetCaret(0);
        window.KeyTextInput("// hi\n".TrimEnd());
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("// hi", tab.Document.GetText(), StringComparison.Ordinal);
        Assert.True(tab.IsDirty);
        Save(window, "editor");
    }

    [AvaloniaFact]
    public async Task Multiple_cursors_from_the_keyboard_edit_together()
    {
        var folder = Directory.CreateTempSubdirectory("coder-multicursor-").FullName;
        try
        {
            var file = Path.Combine(folder, "colors.ts");
            await File.WriteAllTextAsync(file, "const red = 1;\nconst green = red + 1;\nconst blue = red * 2;\n", TestContext.Current.CancellationToken);
            var (window, workbench) = CreateWindow();
            window.Show();
            await workbench.OpenFileAsync(file);
            Dispatcher.UIThread.RunJobs();
            var editor = workbench.Editors.ActiveEditor!.Editor;

            // Ctrl+D three times: the word "red", then its next two occurrences.
            editor.SetCaret(7);
            for (var i = 0; i < 3; i++)
            {
                window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal(3, editor.Selections.Count);
            window.KeyTextInput("crimson");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("const crimson = 1;\nconst green = crimson + 1;\nconst blue = crimson * 2;\n", editor.Document.GetText());
            Assert.Equal("3 cursors", workbench.StatusBar.CursorText);
            Save(window, "multi-cursor");

            // Esc returns to a single cursor.
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, editor.Selections.Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task International_text_renders_and_selects_on_its_glyphs()
    {
        var folder = Directory.CreateTempSubdirectory("coder-international-").FullName;
        try
        {
            var file = Path.Combine(folder, "greetings.ts");
            await File.WriteAllTextAsync(file,
                "// تحية بعدة لغات\n" +
                "const arabic = \"مرحبا بالعالم\";\n" +
                "const chinese = \"你好，世界\";\n" +
                "const japanese = \"こんにちは世界\";\n" +
                "const emoji = \"👋🌍✨\";\n" +
                "const mixed = \"Hello مرحبا 你好 👋\";\n",
                TestContext.Current.CancellationToken);
            var (window, workbench) = CreateWindow();
            window.Show();
            await workbench.OpenFileAsync(file);
            Dispatcher.UIThread.RunJobs();

            var document = workbench.Editors.ActiveEditor!.Document;
            var mixedLine = document.GetLineStart(5);
            var text = document.GetLineText(5);
            workbench.Editors.ActiveEditor.Editor.SetSelection(new Core.Text.Selection(
                mixedLine + text.IndexOf("Hello", StringComparison.Ordinal) + 3,
                mixedLine + text.IndexOf("你好", StringComparison.Ordinal) + 1));
            Save(window, "international");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Terminal_runs_a_real_shell_and_keeps_shell_shortcuts()
    {
        if (!PseudoTerminal.IsSupported)
        {
            Assert.Skip("The terminal needs Windows 10 1809 or later.");
            return;
        }

        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.Backquote, RawInputModifiers.Control);
        Assert.True(workbench.IsTerminalVisible);
        var view = window.GetVisualDescendants().OfType<TerminalView>().Single();
        await WaitUntil(() => view.Session is not null && ScreenText(view).Contains('>', StringComparison.Ordinal),
            () => $"session: {(view.Session is null ? "none" : view.Session.HasExited ? "exited" : "running")}, bounds: {view.Bounds}, screen: [{ScreenText(view).Trim()}]");

        window.KeyTextInput("echo (\"coder\" + \"-terminal\")"); // joined only when the shell runs it
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await WaitUntil(() => ScreenText(view).Contains("coder-terminal", StringComparison.Ordinal),
            () => $"focused: {view.IsFocused}, screen: [{ScreenText(view).Trim()}]");

        // Ctrl+B belongs to the shell while the terminal has focus.
        var sideBarVisible = workbench.IsSideBarVisible;
        window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.Control);
        Assert.Equal(sideBarVisible, workbench.IsSideBarVisible);

        // A second terminal gets its own tab and becomes the shown one.
        var panel = window.GetVisualDescendants().OfType<TerminalPanel>().Single();
        var first = view.Session;
        window.KeyPressQwerty(PhysicalKey.Backquote, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitUntil(() => panel.Sessions.Count == 2, () => $"terminals: {panel.Sessions.Count}");
        Assert.Same(panel.Sessions[1], view.Session);
        Assert.Same(first, panel.Sessions[0]);

        // The terminals are listed top to bottom at the right of the terminal, like VS Code's terminal tabs.
        Dispatcher.UIThread.RunJobs();
        var rows = panel.GetVisualDescendants().OfType<FlatButton>()
            .Where(b => (Avalonia.Automation.AutomationProperties.GetName(b) ?? string.Empty).StartsWith("Terminal ", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, rows.Count);
        Assert.True(rows[1].TranslatePoint(default, panel)!.Value.Y > rows[0].TranslatePoint(default, panel)!.Value.Y, "rows stack vertically");
        Assert.True(rows[0].TranslatePoint(default, panel)!.Value.X > view.Bounds.Right - 1, "the list is right of the terminal");
        Assert.Contains("active", rows[1].Classes);
        Save(window, "terminal");

        // Kill both: killing the shown one shows the remaining one, the last kill hides the panel.
        Button(window, "Kill Terminal 2").Command!.Execute(null);
        Assert.Same(first, Assert.Single(panel.Sessions));
        Assert.Same(first, view.Session);
        Button(window, "Kill Terminal").Command!.Execute(null);
        Assert.Empty(panel.Sessions);
        Assert.False(workbench.IsTerminalVisible);
    }

    [AvaloniaFact]
    public void Clear_search_results_empties_search_and_replace()
    {
        var (_, workbench) = CreateWindow();
        workbench.Search.Query = "color";
        workbench.Search.Replacement = "colour";

        workbench.Search.Clear();

        Assert.Equal(string.Empty, workbench.Search.Query);
        Assert.Equal(string.Empty, workbench.Search.Replacement);
    }

    private static Avalonia.Controls.Button Button(MainWindow window, string name) =>
        window.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == name);

    /// <summary>
    /// A terminal's history has a scroll bar that follows the output and the wheel and scrolls the
    /// terminal when moved. Split Terminal puts a second shell beside the first, in the same group:
    /// both show side by side, and killing one leaves the other.
    /// </summary>
    [AvaloniaFact]
    public async Task Terminal_scrolls_with_its_scroll_bar_and_splits()
    {
        if (!PseudoTerminal.IsSupported)
        {
            Assert.Skip("The terminal needs Windows 10 1809 or later.");
            return;
        }

        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        workbench.IsTerminalVisible = true;
        var panel = window.GetVisualDescendants().OfType<TerminalPanel>().Single();
        await WaitUntil(() => panel.VisiblePanes.Count == 1 && ScreenText(panel.VisiblePanes[0].View).Contains('>', StringComparison.Ordinal), () => "no prompt");
        var pane = panel.VisiblePanes[0];
        var view = pane.View;

        // 200 lines of output: history to scroll through, and the bar shows the bottom.
        window.KeyTextInput("1..200");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await WaitUntil(() => view.ScrollbackLines > 100, () => $"history: {view.ScrollbackLines}");
        Dispatcher.UIThread.RunJobs();
        var bar = pane.ScrollBar;
        Assert.True(bar.IsVisible, "the scroll bar shows when there is history");
        Assert.Equal(view.ScrollbackLines, bar.Maximum);
        Assert.Equal(bar.Maximum, bar.Value);

        // The wheel moves the bar; moving the bar scrolls the terminal.
        var center = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(center, new Vector(0, 2));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(6, view.ScrollOffset);
        Assert.Equal(bar.Maximum - 6, bar.Value);
        bar.SetCurrentValue(Avalonia.Controls.Primitives.RangeBase.ValueProperty, 0d);
        Assert.Equal(view.ScrollbackLines, view.ScrollOffset); // at the top of the history
        Save(window, "terminal-scrolled");

        // Split: a second shell beside the first.
        var first = view.Session;
        await workbench.Commands.Get(Presentation.Commands.CommandIds.SplitTerminal).Execute();
        await WaitUntil(() => panel.VisiblePanes.Count == 2, () => $"panes: {panel.VisiblePanes.Count}");
        Dispatcher.UIThread.RunJobs();
        var (left, right) = (panel.VisiblePanes[0], panel.VisiblePanes[1]);
        Assert.Same(first, left.View.Session);
        Assert.Same(panel.Sessions[1], right.View.Session);
        Assert.Same(right.View.Session, panel.ActiveSession);
        Assert.True(right.TranslatePoint(default, window)!.Value.X > left.TranslatePoint(default, window)!.Value.X + left.Bounds.Width - 10, "side by side");
        await WaitUntil(() => ScreenText(right.View).Contains('>', StringComparison.Ordinal), () => "no prompt in the split");
        Save(window, "terminal-split");

        // A new (unsplit) terminal shows alone; choosing a split one shows its group again.
        await workbench.Commands.Get(Presentation.Commands.CommandIds.NewTerminal).Execute();
        await WaitUntil(() => panel.Sessions.Count == 3 && panel.VisiblePanes.Count == 1, () => $"terminals: {panel.Sessions.Count}, panes: {panel.VisiblePanes.Count}");
        window.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Single(b => (Avalonia.Automation.AutomationProperties.GetName(b) ?? string.Empty).StartsWith("Terminal 1:", StringComparison.Ordinal))
            .Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, panel.VisiblePanes.Count);

        // Kill the left half: the right one stays, alone.
        Button(window, "Kill Terminal 1").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, panel.Sessions.Count);
        Assert.Single(panel.VisiblePanes);
        Assert.Same(panel.Sessions[0], panel.VisiblePanes[0].View.Session);
        window.Close();
    }

    /// <summary>
    /// Ctrl+F in a terminal finds in its output: the newest match is shown first, Enter goes up to older
    /// ones (scrolling back), Shift+Enter comes back down, Esc closes. "Launch Profile…" starts another
    /// shell (Command Prompt), and the default shell can be chosen.
    /// </summary>
    [AvaloniaFact]
    public async Task Terminal_finds_in_its_output_and_starts_other_shells()
    {
        if (!PseudoTerminal.IsSupported)
        {
            Assert.Skip("The terminal needs Windows 10 1809 or later.");
            return;
        }

        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        workbench.IsTerminalVisible = true;
        var panel = window.GetVisualDescendants().OfType<TerminalPanel>().Single();
        await WaitUntil(() => panel.VisiblePanes.Count == 1 && ScreenText(panel.VisiblePanes[0].View).Contains('>', StringComparison.Ordinal), () => "no prompt");
        var pane = panel.VisiblePanes[0];
        var view = pane.View;

        // "find-me" appears twice in the output, 60 lines apart (the command line itself never contains it).
        window.KeyTextInput("echo ('find' + '-me'); 1..60; echo ('find' + '-me')");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await WaitUntil(() => Occurrences(ScreenText(view), "find-me") == 2, () => ScreenText(view));

        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.True(pane.FindBar.IsVisible, "Ctrl+F opened find in the terminal");
        Assert.False(workbench.Find.IsOpen, "not the editor's find");
        pane.FindBar.SetQuery("FIND-ME");
        Dispatcher.UIThread.RunJobs();
        var (matches, current) = pane.FindBar.State;
        Assert.Equal(2, matches.Count);
        Assert.Equal(1, current); // the newest
        Save(window, "terminal-find");

        var finder = window.GetVisualDescendants().OfType<Avalonia.Controls.TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Find in terminal");
        finder.Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, pane.FindBar.State.Current);
        Assert.True(view.ScrollOffset > 0, "scrolled up to the older match");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Shift);
        Assert.Equal(1, pane.FindBar.State.Current);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(pane.FindBar.IsVisible);
        Assert.True(view.IsFocused, "back in the terminal");

        // Another shell: Command Prompt from "Launch Profile…".
        Assert.Contains(workbench.ShellProfiles, p => p.Id == "cmd");
        Button(window, "Launch Profile…").Command!.Execute(null);
        await workbench.Palette.AcceptAsync(workbench.Palette.Items.Single(i => i.Label == "Command Prompt"));
        await WaitUntil(() => panel.Sessions.Count == 2 && ScreenText(panel.VisiblePanes[0].View).Contains("Microsoft Windows", StringComparison.Ordinal),
            () => $"terminals: {panel.Sessions.Count}, screen: {ScreenText(panel.VisiblePanes[0].View)}");

        // The default shell for new terminals.
        workbench.ShowDefaultProfilePicker();
        await workbench.Palette.AcceptAsync(workbench.Palette.Items.Single(i => i.Label == "Command Prompt"));
        Assert.Equal("cmd", workbench.DefaultShellProfile!.Id);
        window.Close();
    }

    /// <summary>
    /// Links in a real shell's output: a file path with a line ("README.md:3:5") and a web address are
    /// underlined under the pointer; Ctrl+Click opens the file in the editor at that line and column,
    /// and the address in the browser. A plain click still just selects.
    /// </summary>
    [AvaloniaFact]
    public async Task Terminal_links_open_files_and_web_addresses()
    {
        if (!PseudoTerminal.IsSupported)
        {
            Assert.Skip("The terminal needs Windows 10 1809 or later.");
            return;
        }

        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        workbench.IsTerminalVisible = true;
        var panel = window.GetVisualDescendants().OfType<TerminalPanel>().Single();
        var opened = new List<Uri>();
        panel.OpenUrl = uri =>
        {
            opened.Add(uri);
            return Task.CompletedTask;
        };
        await WaitUntil(() => panel.VisiblePanes.Count == 1 && ScreenText(panel.VisiblePanes[0].View).Contains('>', StringComparison.Ordinal), () => "no prompt");
        var view = panel.VisiblePanes[0].View;

        // Joined by the shell, so only the output line has the whole link.
        window.KeyTextInput("echo ('README' + '.md:3:5 and https://' + 'example.com/docs')");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await WaitUntil(() => ScreenText(view).Contains("README.md:3:5 and https://example.com/docs", StringComparison.Ordinal), () => ScreenText(view));
        Dispatcher.UIThread.RunJobs();

        var buffer = view.Session!.Emulator.Buffer;
        var line = Enumerable.Range(0, buffer.TotalLines).Last(i => buffer.LineAt(i).Text.StartsWith("README.md:3:5", StringComparison.Ordinal));
        Point At(int column) => view.TranslatePoint(view.PointOf(line, column), window)!.Value;

        // Hover underlines; Ctrl+Click opens the file at line 3, column 5.
        window.MouseMove(At(2));
        Assert.Equal(Coder.Core.Terminal.TerminalLinkKind.File, view.HoverLink?.Kind);
        Save(window, "terminal-link");
        window.MouseDown(At(2), MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(At(2), MouseButton.Left, RawInputModifiers.Control);
        await WaitUntil(() => workbench.Editors.ActiveEditor?.FilePath?.EndsWith("README.md", StringComparison.Ordinal) == true, () => "README.md did not open");
        Assert.Equal(new Coder.Core.Text.TextPosition(2, 4), workbench.Editors.ActiveEditor!.Editor.CaretPosition);

        // The web address goes to the browser.
        var url = buffer.LineAt(line).Text.IndexOf("https://", StringComparison.Ordinal) + 3;
        window.MouseMove(At(url));
        Assert.Equal(Coder.Core.Terminal.TerminalLinkKind.Url, view.HoverLink?.Kind);
        window.MouseDown(At(url), MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(At(url), MouseButton.Left, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([new Uri("https://example.com/docs")], opened);

        // Without Ctrl, a click on a link is an ordinary click (it selects, nothing opens).
        window.MouseDown(At(url), MouseButton.Left);
        window.MouseUp(At(url), MouseButton.Left);
        Assert.Single(opened);
        window.Close();
    }

    /// <summary>
    /// Shell integration with a real PowerShell: each command's prompt line is marked with its exit status
    /// (a failing program's code, success as 0), an empty Enter marks nothing, and the shell's current
    /// folder is known (cd is followed).
    /// </summary>
    [AvaloniaFact]
    public async Task Terminal_marks_each_command_with_its_exit_status()
    {
        if (!PseudoTerminal.IsSupported)
        {
            Assert.Skip("The terminal needs Windows 10 1809 or later.");
            return;
        }

        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        workbench.IsTerminalVisible = true;
        var panel = window.GetVisualDescendants().OfType<TerminalPanel>().Single();
        await WaitUntil(() => panel.VisiblePanes.Count == 1 && panel.VisiblePanes[0].View.Session?.Emulator.HasShellIntegration == true,
            () => $"no shell integration; screen: {(panel.VisiblePanes.Count > 0 ? ScreenText(panel.VisiblePanes[0].View) : "")}", seconds: 20);
        var view = panel.VisiblePanes[0].View;
        var emulator = view.Session!.Emulator;
        IReadOnlyList<Coder.Core.Terminal.CommandMark> Marks() =>
            Enumerable.Range(0, emulator.Buffer.TotalLines).Select(emulator.Buffer.LineAt).Select(l => l.Mark).OfType<Coder.Core.Terminal.CommandMark>().ToList();

        Assert.Equal(RepositoryRoot, emulator.CurrentDirectory);

        void Run(string command)
        {
            window.KeyTextInput(command);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        }

        Run("cmd /c exit 3");
        await WaitUntil(() => Marks().Count >= 2 && Marks()[^2].ExitCode is not null, () => string.Join(", ", Marks().Select(m => m.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-")));
        Assert.Equal(3, Marks()[^2].ExitCode);

        Run(string.Empty); // nothing ran: no status
        Run("cd src");
        await WaitUntil(() => emulator.CurrentDirectory == Path.Combine(RepositoryRoot, "src"), () => $"folder: {emulator.CurrentDirectory}");
        var marks = Marks();
        Assert.Null(marks[^3].ExitCode);  // the empty Enter
        Assert.Equal(0, marks[^2].ExitCode); // cd succeeded
        Assert.Null(marks[^1].ExitCode); // the prompt waiting now
        Save(window, "terminal-exit-status");

        // The failed command's dot explains itself.
        var buffer = emulator.Buffer;
        var failedLine = Enumerable.Range(0, buffer.TotalLines).Single(i => buffer.LineAt(i).Mark?.ExitCode == 3);
        view.ScrollOffset = buffer.TotalLines - buffer.Rows - failedLine; // its prompt at the top
        Assert.Equal("Command failed (exit code 3)", view.CommandMarkTipAt(new Point(7, view.PointOf(failedLine, 0).Y)));

        // Ctrl+Up / Ctrl+Down move between commands (with enough output to scroll through).
        var count = Marks().Count;
        Run("1..80");
        await WaitUntil(() => Marks().Count > count && Marks()[^2].ExitCode == 0, () => "1..80 did not finish");
        view.ScrollOffset = 0;
        view.Focus();
        var before = view.TopVisibleLine;
        for (var i = 0; i < 4; i++)
        {
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Control);
        }

        Assert.True(view.TopVisibleLine < before, "scrolled up to an earlier command");
        Assert.NotNull(buffer.LineAt(view.TopVisibleLine).Mark);

        // A split starts in the shell's current folder.
        await workbench.Commands.Get(Presentation.Commands.CommandIds.SplitTerminal).Execute();
        await WaitUntil(() => panel.VisiblePanes.Count == 2 && panel.VisiblePanes[1].View.Session?.Emulator.CurrentDirectory is not null,
            () => $"split folder: {(panel.VisiblePanes.Count > 1 ? panel.VisiblePanes[1].View.Session?.Emulator.CurrentDirectory : "-")}", seconds: 20);
        Assert.Equal(Path.Combine(RepositoryRoot, "src"), panel.VisiblePanes[1].View.Session!.Emulator.CurrentDirectory);
        window.Close();
    }

    private static string ScreenText(TerminalView view)
    {
        if (view.Session?.Emulator.Buffer is not { } buffer)
        {
            return string.Empty;
        }

        return string.Join("\n", Enumerable.Range(0, buffer.TotalLines).Select(i => buffer.LineAt(i).Text));
    }

    [AvaloniaFact]
    public void Icon_buttons_in_side_bar_headers_are_square()
    {
        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var headerButtons = window.GetVisualDescendants().OfType<Controls.FlatButton>()
            .Where(button => Avalonia.Automation.AutomationProperties.GetName(button) is "New File…" or "New Folder…" or "Refresh Explorer" or "Collapse Folders in Explorer")
            .Where(button => button.IsEffectivelyVisible)
            .ToList();

        Assert.Equal(4, headerButtons.Count);
        Assert.All(headerButtons, button => Assert.Equal(button.Bounds.Width, button.Bounds.Height));
    }

    [AvaloniaFact]
    public void Expanding_and_collapsing_folders_repeatedly_does_not_crash()
    {
        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        var explorer = workbench.Explorer;

        for (var round = 0; round < 3; round++)
        {
            foreach (var name in new[] { "src", "tests", "tools" })
            {
                var folder = explorer.Rows.First(row => row.Name == name && row.Depth == 0);
                explorer.Activate(folder);
                Dispatcher.UIThread.RunJobs();
            }

            foreach (var name in new[] { "src", "tests", "tools" })
            {
                explorer.Activate(explorer.Rows.First(row => row.Name == name && row.Depth == 0));
                Dispatcher.UIThread.RunJobs();
            }
        }

        // Switching to another folder resets the whole list.
        workbench.OpenFolder(Path.Combine(RepositoryRoot, "src"));
        Dispatcher.UIThread.RunJobs();
        explorer.Activate(explorer.Rows.First(row => row.IsDirectory));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Scrolling_through_quick_open_and_command_results_does_not_crash()
    {
        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();

        foreach (var prefix in new[] { string.Empty, ">" })
        {
            workbench.Palette.Open(prefix);
            Dispatcher.UIThread.RunJobs();

            // Walk past the bottom (wraps to the top) so rows scroll out and get recycled.
            for (var i = 0; i < workbench.Palette.Items.Count + 5; i++)
            {
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
            }

            window.KeyTextInput("cs");
            Dispatcher.UIThread.RunJobs();
            workbench.Palette.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Command_palette_lists_commands()
    {
        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();

        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();

        Assert.True(workbench.Palette.IsOpen);
        Assert.NotEmpty(workbench.Palette.Items);
        Save(window, "palette");
    }

    [AvaloniaFact]
    public async Task Find_highlights_matches_and_replace_all_edits_the_document()
    {
        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        await workbench.OpenFileAsync(Path.Combine(RepositoryRoot, "src", "Coder.Core", "Editing", "EditorState.cs"));
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.H, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("Selection");
        Dispatcher.UIThread.RunJobs();

        var find = workbench.Find;
        Assert.True(find.IsOpen);
        Assert.True(find.IsReplaceVisible);
        Assert.True(find.Matches.Count > 5);
        Save(window, "find");

        var document = workbench.Editors.ActiveEditor!.Document;
        var replaced = find.Matches.Count;
        var existing = Occurrences(document.GetText(), "Marker42");
        find.Replacement = "Marker42";
        find.ReplaceAll();

        Assert.Equal(existing + replaced, Occurrences(document.GetText(), "Marker42"));
        Assert.Empty(find.Matches);
    }

    [AvaloniaFact]
    public async Task Search_in_files_streams_results_and_opens_a_match()
    {
        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();

        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control | RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("TryExtendPreviousInsert");

        var search = workbench.Search;
        await WaitUntil(() => !search.IsSearching && search.Rows.Count > 0);
        Assert.Equal(SideBarView.Search, workbench.ActiveSideBarView);
        Save(window, "search");

        // This test file contains the query too, so pick the result from the real source file.
        var match = search.Rows.OfType<SearchMatchRowViewModel>().First(row => row.File.FileName == "PieceTable.cs");
        await search.ActivateAsync(match);
        var tab = workbench.Editors.ActiveEditor!;
        Assert.Equal("PieceTable.cs", tab.Title);
        Assert.Equal("TryExtendPreviousInsert", tab.Editor.SelectedText);
    }

    [AvaloniaFact]
    public void Opening_a_folder_records_it_and_closing_saves_the_session()
    {
        var store = new MemorySettingsStore();
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), store);
        var window = new MainWindow(workbench);
        window.Show();

        workbench.OpenFolder(RepositoryRoot);
        workbench.EditorFontSize = 17;
        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(store.Saved);
        Assert.Equal(RepositoryRoot, store.Saved.RecentFolders[0]);
        Assert.Equal(17, store.Saved.EditorFontSize);
        Assert.NotNull(store.Saved.Window);
    }

    [AvaloniaFact]
    public async Task Replace_in_files_changes_closed_files_on_disk_and_open_files_in_the_editor()
    {
        var folder = Directory.CreateTempSubdirectory("coder-replace-ui-").FullName;
        try
        {
            var closedFile = Path.Combine(folder, "closed.txt");
            var openFile = Path.Combine(folder, "open.cs");
            await File.WriteAllTextAsync(closedFile, "color = red;\r\nborder-color: red;\r\n");
            await File.WriteAllTextAsync(openFile, "var color = Color.Red; // red");

            var (window, workbench) = CreateWindow();
            workbench.OpenFolder(folder);
            window.Show();
            await workbench.OpenFileAsync(openFile);

            var search = workbench.Search;
            workbench.Commands.Get(Presentation.Commands.CommandIds.ReplaceInFiles).Execute().Wait();
            search.Query = "red";
            search.Replacement = "blue";
            await WaitUntil(() => search.CanReplaceAll);
            Save(window, "replace-preview");

            await search.ReplaceAllAsync();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("color = blue;\r\nborder-color: blue;\r\n", await File.ReadAllTextAsync(closedFile));
            var tab = workbench.Editors.ActiveEditor!;
            Assert.Equal("var color = Color.blue; // blue", tab.Document.GetText());
            Assert.True(tab.IsDirty);
            Assert.Equal("var color = Color.Red; // red", await File.ReadAllTextAsync(openFile));
            Assert.StartsWith("Replaced 4 occurrences in 2 files", search.Summary, StringComparison.Ordinal);

            tab.Editor.Undo();
            Assert.Equal("var color = Color.Red; // red", tab.Document.GetText());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Lets background work (searches) finish while the UI dispatcher keeps running.</summary>
    private static async Task WaitUntil(Func<bool> condition, Func<string>? describe = null, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(condition(), "Timed out waiting for the condition. " + describe?.Invoke());
    }

    private static int Occurrences(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    private static (MainWindow Window, WorkbenchViewModel Workbench) CreateWindow()
    {
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore());
        return (new MainWindow(workbench) { Width = 1280, Height = 800 }, workbench);
    }

    private static void Save(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var directory = Path.Combine(RepositoryRoot, "artifacts", "screenshots");
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, $"{name}.png"));
    }

    /// <summary>Walks up from this source file, so it works wherever the build output is placed.</summary>
    private static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Coder.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        public UserSettings? Saved { get; private set; }

        public UserSettings Load() => Saved ?? new UserSettings();

        public void Save(UserSettings settings) => Saved = settings;
    }

    private sealed class NoDialogs : IDialogService
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickSaveLocationAsync(string suggestedName) => Task.FromResult<string?>(null);

        public Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(string fileName) => Task.FromResult(UnsavedChangesChoice.DontSave);

        /// <summary>The questions asked so far (each is answered yes).</summary>
        public List<string> Asked { get; } = [];

        public Task<bool> ConfirmAsync(string message, string detail, string confirmLabel)
        {
            Asked.Add(message);
            return Task.FromResult(true);
        }
    }
}
