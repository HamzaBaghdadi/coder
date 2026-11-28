using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Workbench;
using Coder.Core.Extensions;
using Coder.Core.Files;
using Coder.Core.Lsp;
using Coder.Presentation.Editors;
using Coder.Presentation.Extensions;
using Coder.Presentation.Services;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// An extension with a program: Coder asks once before running it, starts it with <c>initialize</c>, puts
    /// its manifest command in the palette (running it reaches the program), answers its Editor API calls
    /// (open a file at a range, read the selection, a diff the user accepts or closes, a terminal with
    /// variables), tells it when the selection moves, and stops it before uninstalling.
    /// </summary>
    [AvaloniaFact]
    public async Task Extension_programs_run_with_consent_and_use_the_editor_api()
    {
        var root = Directory.CreateTempSubdirectory("coder-host-").FullName;
        using var http = new HttpClient();
        try
        {
            var store = new ExtensionStore(Path.Combine(root, "installed"), http);
            InstallProgramExtension(store.Folder);
            var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            var file = Path.Combine(work, "notes.txt");
            await File.WriteAllTextAsync(file, "alpha\nbeta\ngamma\n", TestContext.Current.CancellationToken);

            var dialogs = new NoDialogs();
            var workbench = new WorkbenchViewModel(new LocalFileSystem(), dialogs, new MemorySettingsStore(), extensionStore: store);
            var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
            workbench.OpenFolder(work);
            window.Show();
            var launcher = new FakeLauncher();
            var host = workbench.EnableExtensionHost(launcher);
            Assert.True(workbench.Commands.Contains("helper.hello")); // in the palette before it runs

            await host.StartAsync();
            var program = launcher.Programs.Single();
            Assert.Equal(["Allow Acme Helper to run on this computer?"], dialogs.Asked);
            var initialize = await program.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(work, initialize.GetProperty("workspaceFolders")[0].GetString());
            Assert.Equal("acme.helper", initialize.GetProperty("extension").GetProperty("id").GetString());
            Assert.Equal("1", launcher.Environments.Single()["CODER_PID"] is { } pid && int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture) == Environment.ProcessId ? "1" : "0");

            // Its command runs in the program.
            await workbench.Commands.ExecuteAsync("helper.hello");
            Assert.Equal("helper.hello", await program.Commands.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

            // Open a file with a range selected; the selection reaches the program.
            var opened = await program.Call("editor/open", new JsonObject
            {
                ["path"] = file,
                ["range"] = new JsonObject { ["start"] = Position(1, 0), ["end"] = Position(1, 4) },
            });
            Assert.Equal(3 + 1, opened.GetProperty("lineCount").GetInt32());
            var tab = workbench.Editors.ActiveEditor!;
            Assert.Equal("beta", tab.Editor.SelectedText);
            var selection = await program.Call("editor/selection", null);
            Assert.Equal("beta", selection.GetProperty("text").GetString());
            await WaitUntil(() => program.LastSelectionText == "beta", () => $"last selection: {program.LastSelectionText}");

            // A proposal the user accepts is written through the open editor and saved.
            var review = program.Call("diff/review", new JsonObject { ["path"] = file, ["proposed"] = "alpha\nBETA\ngamma\n", ["title"] = "Proposed: notes" });
            await WaitUntil(() => workbench.Editors.ActiveTab is DiffTabViewModel { Review.IsPending: true });
            Dispatcher.UIThread.RunJobs();
            Save(window, "extension-diff-review");
            Button(window, "Accept change to notes.txt").Command!.Execute(null);
            var accepted = await review.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(accepted.GetProperty("accepted").GetBoolean());
            Assert.Equal("alpha\nBETA\ngamma\n", (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n"));
            Assert.Equal("alpha\nBETA\ngamma\n", tab.Document.GetText().ReplaceLineEndings("\n"));
            Assert.DoesNotContain(workbench.Editors.Tabs, t => t is DiffTabViewModel);

            // A proposal closed by name (as Claude's close_tab does) is a reject; nothing changes.
            var closedReview = program.Call("diff/review", new JsonObject { ["path"] = file, ["proposed"] = "nope\n", ["title"] = "Proposed: again" });
            await WaitUntil(() => workbench.Editors.Tabs.Any(t => t is DiffTabViewModel { Review.IsPending: true }));
            var closed = await program.Call("diff/close", new JsonObject { ["title"] = "Proposed: again" });
            Assert.Equal(1, closed.GetProperty("closed").GetInt32());
            Assert.False((await closedReview.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).GetProperty("accepted").GetBoolean());
            Assert.Equal("alpha\nBETA\ngamma\n", (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n"));

            // A terminal with variables; the list of open editors; a status message.
            TerminalRequest? terminal = null;
            workbench.TerminalRequested += (_, request) => terminal = request;
            await program.Call("terminal/create", new JsonObject { ["name"] = "Helper", ["command"] = "echo hi", ["env"] = new JsonObject { ["HELPER_PORT"] = "1234" } });
            Assert.Equal(("Helper", "echo hi", "1234"), (terminal?.Name, terminal?.CommandLine, terminal?.Environment["HELPER_PORT"]));
            var editors = await program.Call("editor/list", null);
            Assert.Contains(editors.GetProperty("editors").EnumerateArray(), e => e.GetProperty("path").GetString() == file);
            await program.Call("window/showMessage", new JsonObject { ["text"] = "Helper says hi" });
            Assert.Equal("Helper says hi", workbench.StatusBar.Message);

            // A program run directly in a terminal: its arguments pass as given, and "command" is ignored.
            await program.Call("terminal/create", new JsonObject
            {
                ["name"] = "Tool",
                ["command"] = "ignored",
                ["program"] = "tool.exe",
                ["args"] = new JsonArray("--flag", "two words", "say \"hi\""),
            });
            Assert.Equal(("tool.exe", "tool.exe --flag \"two words\" \"say \\\"hi\\\"\""), (terminal?.Program, terminal?.ProgramCommandLine));
            await Assert.ThrowsAsync<LanguageServerException>(() => program.Call("terminal/create", new JsonObject { ["args"] = new JsonArray("x") }));
            await Assert.ThrowsAsync<LanguageServerException>(() => program.Call("terminal/create", new JsonObject { ["program"] = Path.Combine(root, "missing.exe") }));

            // A question with buttons: the action picked comes back as sent, with the extension named for the user.
            dialogs.NextChoice = 1;
            var answer = await program.Call("window/showMessageRequest", new JsonObject
            {
                ["message"] = "Install the helper tool?",
                ["detail"] = "It takes a minute.",
                ["actions"] = new JsonArray(new JsonObject { ["title"] = "Install" }, new JsonObject { ["title"] = "Not now", ["id"] = "later" }),
            });
            Assert.Equal(("Not now", "later"), (answer.GetProperty("title").GetString(), answer.GetProperty("id").GetString()));
            Assert.Equal(("It takes a minute.\n\nFrom Acme Helper.", "Install,Not now"), (dialogs.Choices[^1].Detail, string.Join(',', dialogs.Choices[^1].Choices)));
            dialogs.NextChoice = null;
            var dismissed = await program.Call("window/showMessageRequest", new JsonObject { ["message"] = "Still there?", ["actions"] = new JsonArray(new JsonObject { ["title"] = "Yes" }) });
            Assert.Equal(JsonValueKind.Null, dismissed.ValueKind);
            await Assert.ThrowsAsync<LanguageServerException>(() => program.Call("window/showMessageRequest", new JsonObject { ["actions"] = new JsonArray() }));

            // Bad calls come back as errors, not crashes.
            await Assert.ThrowsAsync<LanguageServerException>(() => program.Call("editor/open", new JsonObject { ["path"] = "relative.txt" }));

            // Uninstalling stops the program first, then removes its command.
            await store.UninstallAsync("acme.helper");
            Assert.True(program.ShutdownRequested);
            Assert.Empty(host.RunningIds);
            await WaitUntil(() => !workbench.Commands.Contains("helper.hello"));
            window.Close();
        }
        finally
        {
            DeleteEventually(root); // the terminal it opened may still hold the folder
        }
    }

    /// <summary>
    /// An extension's view is a tab in the secondary side bar beside Coder Agent. Showing it runs the view's
    /// command; the program opens its terminal there (with its variables and a message), a second request only
    /// shows it again, and uninstalling removes the tab.
    /// </summary>
    [AvaloniaFact]
    public async Task Extension_views_are_tabs_in_the_secondary_side_bar_with_a_terminal()
    {
        var root = Directory.CreateTempSubdirectory("coder-host-view-").FullName;
        using var http = new HttpClient();
        try
        {
            var store = new ExtensionStore(Path.Combine(root, "installed"), http);
            InstallProgramExtension(store.Folder);
            var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore(), extensionStore: store);
            var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
            workbench.OpenFolder(Directory.CreateDirectory(Path.Combine(root, "work")).FullName);
            window.Show();
            var launcher = new FakeLauncher();
            var host = workbench.EnableExtensionHost(launcher);
            await host.StartAsync();
            var program = launcher.Programs.Single();
            await program.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();

            // The tab is there; showing it asks the program, which opens its terminal in the view.
            var view = Assert.Single(workbench.SecondaryViews);
            Assert.Equal(("helper.view", "Helper View"), (view.Id, view.Title));
            workbench.IsSecondarySideBarVisible = true;
            Dispatcher.UIThread.RunJobs();
            Button(window, "Helper View").Command!.Execute(null);
            Assert.Equal("helper.view", workbench.ActiveSecondaryView);
            Assert.Equal("helper.showView", await program.Commands.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            var sideBar = window.GetVisualDescendants().OfType<SecondarySideBar>().Single();
            await WaitUntil(() => sideBar.ExtensionView("helper.view")?.Session is not null && view.HasTerminal, () => "no terminal in the view");
            var session = sideBar.ExtensionView("helper.view")!.Session!;
            string Screen() => string.Concat(Enumerable.Range(0, session.Emulator.Buffer.TotalLines).Select(i => session.Emulator.Buffer.LineAt(i).Text.TrimEnd()));
            await WaitUntil(() => Regex.Count(Screen(), "helper-view-ok") >= 2, Screen); // the typed command, then its output (lines may wrap)
            Assert.Contains(window.GetVisualDescendants().OfType<Avalonia.Controls.SelectableTextBlock>(), t => t.Text == "Welcome to Helper" && t.IsEffectivelyVisible); // the message, above the terminal
            Assert.False(workbench.IsTerminalVisible); // not in the bottom panel
            Save(window, "extension-side-bar-view");

            // Coder Agent's tab is still first; asking for the view again only shows the running terminal.
            Button(window, "Coder Agent").Command!.Execute(null);
            Assert.Null(workbench.ActiveSecondaryView);
            await workbench.Commands.ExecuteAsync("helper.showView");
            await WaitUntil(() => workbench.ActiveSecondaryView == "helper.view");
            Assert.Same(session, sideBar.ExtensionView("helper.view")!.Session);

            // Uninstalled: the tab goes, and the side bar is back on Coder Agent.
            await store.UninstallAsync("acme.helper");
            await WaitUntil(() => workbench.SecondaryViews.Count == 0 && sideBar.ExtensionView("helper.view") is null);
            Assert.Null(workbench.ActiveSecondaryView);
            window.Close();
        }
        finally
        {
            DeleteEventually(root);
        }
    }

    /// <summary>Refused: the program doesn't start, and no one is asked again at start-up; running its command asks again.</summary>
    [AvaloniaFact]
    public async Task A_refused_program_does_not_run_until_allowed()
    {
        var root = Directory.CreateTempSubdirectory("coder-host-").FullName;
        using var http = new HttpClient();
        try
        {
            var store = new ExtensionStore(Path.Combine(root, "installed"), http);
            InstallProgramExtension(store.Folder);
            var dialogs = new ScriptedDialogs(false, true);
            var workbench = new WorkbenchViewModel(new LocalFileSystem(), dialogs, new MemorySettingsStore(), extensionStore: store);
            var launcher = new FakeLauncher();
            var host = workbench.EnableExtensionHost(launcher);

            await host.StartAsync();
            Assert.Empty(launcher.Programs);
            Assert.Single(dialogs.Asked);

            await workbench.Commands.ExecuteAsync("helper.hello"); // asks again, now allowed
            Assert.Equal(2, dialogs.Asked.Count);
            var program = Assert.Single(launcher.Programs);
            Assert.Equal("helper.hello", await program.Commands.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            await host.DisposeAsync();
            Assert.True(program.ShutdownRequested);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A program that dies is started again (a few times), and stays allowed.</summary>
    [AvaloniaFact]
    public async Task A_crashed_program_is_restarted()
    {
        var root = Directory.CreateTempSubdirectory("coder-host-").FullName;
        using var http = new HttpClient();
        try
        {
            var store = new ExtensionStore(Path.Combine(root, "installed"), http);
            InstallProgramExtension(store.Folder);
            var dialogs = new NoDialogs();
            var workbench = new WorkbenchViewModel(new LocalFileSystem(), dialogs, new MemorySettingsStore(), extensionStore: store);
            var launcher = new FakeLauncher();
            var host = workbench.EnableExtensionHost(launcher);
            await host.StartAsync();
            await launcher.Programs.Single().Initialized.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            launcher.Programs.Single().Crash();
            await WaitUntil(() => launcher.Programs.Count == 2 && host.RunningIds.Count == 1, () => $"{launcher.Programs.Count} started, {host.RunningIds.Count} running");
            await launcher.Programs[1].Initialized.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Single(dialogs.Asked);
            await host.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonObject Position(int line, int character) => new() { ["line"] = line, ["character"] = character };

    /// <summary>An installed extension whose manifest declares a program for this platform and one command.</summary>
    private static void InstallProgramExtension(string extensionsFolder)
    {
        var folder = Path.Combine(extensionsFolder, "acme.helper-1.0.0");
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "bin", "helper.exe"), "the fake launcher never runs this");
        File.WriteAllText(Path.Combine(folder, "package.json"), $$"""
            { "publisher": "acme", "name": "helper", "version": "1.0.0", "displayName": "Acme Helper",
              "coder": { "program": { "{{Platform.RuntimeId}}": "bin/helper.exe" }, "views": [ { "id": "helper.view", "title": "Helper View", "location": "secondarySidebar", "command": "helper.showView" } ] },
              "contributes": { "commands": [ { "command": "helper.hello", "title": "Say Hello", "category": "Helper" }, { "command": "helper.showView", "title": "Show View", "category": "Helper" } ] } }
            """);
    }

    private sealed class FakeLauncher : IExtensionLauncher
    {
        public List<FakeProgram> Programs { get; } = [];

        public List<IReadOnlyDictionary<string, string?>> Environments { get; } = [];

        public ILanguageServerTransport Start(InstalledExtension extension, IReadOnlyDictionary<string, string?> environment)
        {
            var program = new FakeProgram();
            Programs.Add(program);
            Environments.Add(environment);
            return program.CoderSide;
        }
    }

    /// <summary>An extension program on the other end of in-memory pipes, scripted for the tests.</summary>
    private sealed class FakeProgram : IDisposable
    {
        private readonly JsonRpcConnection _connection;
        private readonly Pipe _toCoder = new();

        public FakeProgram()
        {
            var toProgram = new Pipe();
            var toCoder = _toCoder;
            CoderSide = new Transport(toCoder, toProgram);
            _connection = new JsonRpcConnection(toProgram, toCoder);
            _connection.OnRequest("initialize", p =>
            {
                Initialized.TrySetResult(p.Clone());
                return new JsonObject();
            });
            _connection.OnRequest("commands/execute", p =>
            {
                var command = p.GetProperty("command").GetString()!;
                Commands.Writer.TryWrite(command);
                if (command == "helper.showView")
                {
                    // as Claude Code's program does: its own interface, in a terminal in its side-bar tab
                    _ = Call("terminal/create", new JsonObject
                    {
                        ["name"] = "Helper",
                        ["view"] = "helper.view",
                        ["command"] = "echo helper-view-ok",
                        ["message"] = "Welcome to Helper",
                        ["env"] = new JsonObject { ["HELPER_TOKEN"] = "42" },
                    });
                }

                return null;
            });
            _connection.OnRequest("shutdown", _ =>
            {
                ShutdownRequested = true;
                return null;
            });
            _connection.OnNotification("editor/selectionChanged", p => LastSelectionText = p.GetProperty("text").GetString());
            _connection.Start();
        }

        public ILanguageServerTransport CoderSide { get; }

        public TaskCompletionSource<JsonElement> Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Channel<string> Commands { get; } = Channel.CreateUnbounded<string>();

        public bool ShutdownRequested { get; private set; }

        public string? LastSelectionText { get; private set; }

        public Task<JsonElement> Call(string method, JsonNode? parameters) => _connection.RequestAsync(method, parameters);

        public void Dispose() => _connection.Dispose();

        /// <summary>Ends the connection as a crash would.</summary>
        public void Crash()
        {
            _connection.Dispose();
            _toCoder.Dispose(); // its output ends, as when a process exits
        }

        private sealed class Transport(Stream input, Stream output) : ILanguageServerTransport
        {
            public Stream Input => input;

            public Stream Output => output;

            public string RecentErrorOutput => string.Empty;

            public void Dispose()
            {
                input.Dispose();
                output.Dispose();
            }
        }
    }

    /// <summary>A one-way in-memory pipe: bytes written become readable, in order; disposing ends it.</summary>
    private sealed class Pipe : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[] _current = [];
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                {
                    return 0;
                }

                _current = await _chunks.Reader.ReadAsync(cancellationToken);
                _position = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _position);
            _current.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _chunks.Writer.TryWrite(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) => _chunks.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray());

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _chunks.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }

    /// <summary>Answers each yes/no question in turn.</summary>
    private sealed class ScriptedDialogs(params bool[] answers) : IDialogService
    {
        public List<string> Asked { get; } = [];

        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickSaveLocationAsync(string suggestedName) => Task.FromResult<string?>(null);

        public Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(string fileName) => Task.FromResult(UnsavedChangesChoice.DontSave);

        public Task<bool> ConfirmAsync(string message, string detail, string confirmLabel)
        {
            Asked.Add(message);
            return Task.FromResult(answers[Math.Min(Asked.Count - 1, answers.Length - 1)]);
        }

        public Task<int?> ChooseAsync(string message, string detail, IReadOnlyList<string> choices, CancellationToken cancellationToken = default)
        {
            Asked.Add(message);
            return Task.FromResult<int?>(null);
        }
    }
}
