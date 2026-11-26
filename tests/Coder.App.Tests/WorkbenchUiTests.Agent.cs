using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Workbench;
using Coder.App.Workbench.Agent;
using Coder.Core.Ai;
using Coder.Core.Files;
using Coder.Presentation.Agent;
using Coder.Presentation.Settings;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// The secondary side bar is Coder Agent: an empty state, then (Enter sends) the user's message and
    /// the reply streaming in as Markdown, with headings, lists, inline code and a colored code block.
    /// The request carries the system prompt, the message and the provider's key.
    /// </summary>
    [AvaloniaFact]
    public async Task Agent_chat_streams_a_markdown_reply()
    {
        using var server = new FakeAgentServer(
        [
            Sse("## Plan", "\n\n- Read **the** file\n- Use `dotnet test`\n\n", "```csharp\nvar x = 1;\n```\nDone."),
        ]);
        var (window, workbench, agent) = CreateAgentWindow(server);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var view = window.GetVisualDescendants().OfType<AgentView>().Single();
        Assert.True(view.IsEffectivelyVisible);
        Assert.Contains(view.GetVisualDescendants().OfType<StackPanel>(), p => p.Classes.Contains("agent-empty") && p.IsVisible);
        Assert.Equal("test-model · Test AI", agent.ModelLabel);
        Save(window, "agent-empty");

        view.Input.Focus();
        view.Input.Text = "How do I test?";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await WaitUntil(() => !agent.IsBusy && agent.Items.OfType<AssistantMessageItem>().Any(), () => string.Join(" | ", agent.Items));

        Assert.Equal(string.Empty, agent.Input);
        Assert.Equal("How do I test?", Assert.IsType<UserMessageItem>(agent.Items[0]).Text);
        var reply = Assert.IsType<AssistantMessageItem>(agent.Items[1]);
        Assert.False(reply.IsStreaming);
        Dispatcher.UIThread.RunJobs();
        var markdown = view.GetVisualDescendants().OfType<MarkdownView>().Single();
        Assert.Equal("Plan\nRead the file\nUse dotnet test\nvar x = 1;\nDone.", markdown.Text);
        Assert.Contains(markdown.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text == "var") == true);
        Save(window, "agent-reply");

        using var body = JsonDocument.Parse(server.ChatBodies.Single());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("No folder is open", messages[0].GetProperty("content").GetString());
        Assert.Equal("How do I test?", messages[1].GetProperty("content").GetString());
        Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("Bearer test-key", server.Authorization);
        Assert.False(body.RootElement.TryGetProperty("tools", out _)); // no folder, no tools

        // New Chat clears the conversation.
        await workbench.Commands.ExecuteAsync(Presentation.Commands.CommandIds.AgentNewChat);
        Assert.Empty(agent.Items);
    }

    /// <summary>
    /// With a folder open the agent edits files: its edit shows as a diff card (+/− counts, the changed
    /// lines) and nothing changes until Accept; Accept writes the file, the agent hears it was accepted and
    /// finishes. A second edit is rejected (the file stays), and a command it proposes is skipped (never run).
    /// </summary>
    [AvaloniaFact]
    public async Task Agent_edits_are_reviewed_as_diffs_and_commands_need_approval()
    {
        var folder = Directory.CreateTempSubdirectory("coder-agent-ui-").FullName;
        var file = Path.Combine(folder, "greet.txt");
        await File.WriteAllTextAsync(file, "Hello\nWorld\n", TestContext.Current.CancellationToken);
        using var server = new FakeAgentServer(
        [
            Tool("edit_file", new { path = "greet.txt", old_text = "Hello", new_text = "Hello, Coder" }),
            Tool("edit_file", new { path = "greet.txt", old_text = "World", new_text = "Moon" }),
            Tool("run_command", new { command = "dotnet build" }),
            Sse("All done."),
        ]);
        var runner = new RecordingCommandRunner();
        var (window, workbench, agent) = CreateAgentWindow(server, runner);
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            agent.Input = "Greet Coder";
            var turn = agent.SendAsync();
            await WaitUntil(() => agent.Items.OfType<ToolCallItem>().FirstOrDefault()?.Proposal is EditProposalItem);
            var first = (EditProposalItem)agent.Items.OfType<ToolCallItem>().First().Proposal!;
            Assert.Equal("greet.txt", first.RelativePath);
            Assert.Equal((1, 1), (first.Added, first.Removed));
            Assert.Contains(first.Lines, l => l.Kind == DiffLineKind.Added && l.Text == "Hello, Coder");
            Assert.Contains(first.Lines, l => l.Kind == DiffLineKind.Removed && l.Text == "Hello");
            Assert.Equal(ToolStatus.WaitingForUser, agent.Items.OfType<ToolCallItem>().First().Status);
            Assert.Equal("Hello\nWorld\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)); // nothing yet
            Dispatcher.UIThread.RunJobs();
            Assert.Single(window.GetVisualDescendants().OfType<Control>(), c => c.Classes.Contains("edit-proposal"));
            Save(window, "agent-edit-proposal");

            VisibleButton(window, "Accept change to greet.txt").Command!.Execute(null);
            await WaitUntil(() => agent.Items.OfType<ToolCallItem>().Count() >= 2 && agent.Items.OfType<ToolCallItem>().ElementAt(1).Proposal is EditProposalItem);
            Assert.Equal("Hello, Coder\nWorld\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
            Assert.Equal(ProposalState.Accepted, first.State);
            Assert.Equal(ToolStatus.Succeeded, agent.Items.OfType<ToolCallItem>().First().Status);

            VisibleButton(window, "Reject change to greet.txt").Command!.Execute(null);
            await WaitUntil(() => agent.Items.OfType<ToolCallItem>().Count() >= 3 && agent.Items.OfType<ToolCallItem>().ElementAt(2).Proposal is CommandProposalItem);
            Assert.Equal("Hello, Coder\nWorld\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
            Dispatcher.UIThread.RunJobs();
            Save(window, "agent-command-proposal");

            VisibleButton(window, "Skip command").Command!.Execute(null);
            await turn;
            Assert.Empty(runner.Ran);
            Assert.Equal("All done.", agent.Items.OfType<AssistantMessageItem>().Last().Text);
            Assert.Equal([ToolStatus.Succeeded, ToolStatus.Failed, ToolStatus.Failed], agent.Items.OfType<ToolCallItem>().Select(t => t.Status));

            // What the model heard after each decision.
            Assert.Contains("accepted", server.ChatBodies[1]);
            Assert.Contains("rejected", server.ChatBodies[2]);
            Assert.Contains("declined to run", server.ChatBodies[3]);
            Assert.Contains("\"tools\"", server.ChatBodies[0]);
            Assert.Contains(folder.Replace("\\", "\\\\", StringComparison.Ordinal), server.ChatBodies[0]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Attachments: files, folders and images become chips (a binary file is refused with a notice), a
    /// chip's × removes it, and sending puts the file's text in a &lt;file&gt; tag and the image in the request.
    /// </summary>
    [AvaloniaFact]
    public async Task Agent_attachments_show_as_chips_and_go_with_the_message()
    {
        var folder = Directory.CreateTempSubdirectory("coder-agent-attach-").FullName;
        using var server = new FakeAgentServer([Sse("Seen.")]);
        var (window, workbench, agent) = CreateAgentWindow(server);
        try
        {
            var notes = Path.Combine(folder, "notes.md");
            await File.WriteAllTextAsync(notes, "# Notes\nremember the milk", TestContext.Current.CancellationToken);
            var binary = Path.Combine(folder, "tool.exe");
            await File.WriteAllBytesAsync(binary, [0x4D, 0x5A, 0, 0, 1], TestContext.Current.CancellationToken);
            var sub = Directory.CreateDirectory(Path.Combine(folder, "docs")).FullName;
            await File.WriteAllTextAsync(Path.Combine(sub, "a.txt"), "a", TestContext.Current.CancellationToken);
            var image = Path.Combine(folder, "shot.png");
            SavePng(image);
            workbench.OpenFolder(folder);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            await agent.AddPathsAsync([notes, binary, sub, image, notes]);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["notes.md", "docs", "shot.png"], agent.Attachments.Select(a => a.Name));
            Assert.Equal(AttachmentKind.Image, agent.Attachments[2].Kind);
            Assert.Contains("tool.exe is a binary file", agent.Notice);
            var chips = window.GetVisualDescendants().OfType<Control>().Where(c => c.Classes.Contains("attachment-chip")).ToList();
            Assert.Equal(3, chips.Count);
            Save(window, "agent-attachments");

            Button(window, "Remove docs").Command!.Execute(Button(window, "Remove docs").CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["notes.md", "shot.png"], agent.Attachments.Select(a => a.Name));

            agent.Input = "What is this?";
            await agent.SendAsync();
            Assert.Empty(agent.Attachments);
            Assert.Equal(2, Assert.IsType<UserMessageItem>(agent.Items[0]).Attachments.Count);
            using var body = JsonDocument.Parse(server.ChatBodies.Single());
            var content = body.RootElement.GetProperty("messages")[1].GetProperty("content");
            var text = content[0].GetProperty("text").GetString()!;
            Assert.StartsWith("What is this?", text);
            Assert.Contains("<file path=\"notes.md\">\n# Notes\nremember the milk\n</file>", text);
            Assert.StartsWith("data:image/png;base64,", content[1].GetProperty("image_url").GetProperty("url").GetString());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Files dragged from Windows onto the chat: a drop hint shows while over it, and dropping attaches
    /// them; a BMP (which the APIs refuse) is converted to PNG. Text dropped goes into the input.
    /// </summary>
    [AvaloniaFact]
    public async Task Agent_accepts_files_and_images_dropped_from_outside()
    {
        var folder = Directory.CreateTempSubdirectory("coder-agent-drop-").FullName;
        using var server = new FakeAgentServer([]);
        var (window, _, agent) = CreateAgentWindow(server);
        try
        {
            var readme = Path.Combine(folder, "readme.txt");
            await File.WriteAllTextAsync(readme, "hi", TestContext.Current.CancellationToken);
            var bmp = Path.Combine(folder, "pixel.bmp");
            await File.WriteAllBytesAsync(bmp, Bmp2x2(), TestContext.Current.CancellationToken);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var view = window.GetVisualDescendants().OfType<AgentView>().Single();
            var point = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), window)!.Value;

            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(await LocalFile(window, readme)));
            data.Add(DataTransferItem.CreateFile(await LocalFile(window, bmp)));
            window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
            window.DragDrop(point, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.IsDropHintVisible);
            Save(window, "agent-drop-hint");

            window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
            await WaitUntil(() => agent.Attachments.Count == 2);
            Assert.False(view.IsDropHintVisible);
            Assert.Equal(["pixel.png", "readme.txt"], agent.Attachments.Select(a => a.Name).Order());
            var png = agent.Attachments.Single(a => a.Kind == AttachmentKind.Image);
            Assert.Equal("image/png", png.MediaType);
            Assert.Equal([0x89, 0x50, 0x4E, 0x47], png.Data![..4]);

            var text = new DataTransfer();
            text.Add(DataTransferItem.CreateText("dropped words"));
            window.DragDrop(point, RawDragEventType.DragEnter, text, DragDropEffects.Copy, RawInputModifiers.None);
            window.DragDrop(point, RawDragEventType.Drop, text, DragDropEffects.Copy, RawInputModifiers.None);
            await WaitUntil(() => agent.Input == "dropped words");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A file dragged from the explorer, or a file's tab dragged from the tab strip, is attached when dropped on the chat (not moved).</summary>
    [AvaloniaFact]
    public async Task Agent_accepts_files_dragged_from_the_explorer_and_tabs()
    {
        var folder = Directory.CreateTempSubdirectory("coder-agent-drag-").FullName;
        using var server = new FakeAgentServer([]);
        var (window, workbench, agent) = CreateAgentWindow(server);
        try
        {
            var app = Path.Combine(folder, "app.ts");
            var util = Path.Combine(folder, "util.ts");
            await File.WriteAllTextAsync(app, "export const app = 1;\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(util, "export const util = 2;\n", TestContext.Current.CancellationToken);
            workbench.OpenFolder(folder);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var view = window.GetVisualDescendants().OfType<AgentView>().Single();
            var chat = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), window)!.Value;

            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => Avalonia.Automation.AutomationProperties.GetName(l) == "Files");
            var row = (Control)list.ContainerFromItem(workbench.Explorer.Rows.Single(r => r.Name == "app.ts"))!;
            var start = row.TranslatePoint(new Point(30, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Point(20, 0));
            window.MouseMove(chat);
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.IsDropHintVisible);
            window.MouseUp(chat, MouseButton.Left);
            await WaitUntil(() => agent.Attachments.Count == 1);
            Assert.False(view.IsDropHintVisible);
            Assert.Equal(app, agent.Attachments[0].Path);
            Assert.True(File.Exists(app)); // attached, not moved

            await workbench.OpenFileAsync(util);
            Dispatcher.UIThread.RunJobs();
            var strip = window.GetVisualDescendants().OfType<EditorTabStrip>().First();
            var tab = strip.GetVisualDescendants().OfType<Controls.ChamferBorder>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "util.ts");
            var tabPoint = tab.TranslatePoint(new Point(20, tab.Bounds.Height / 2), window)!.Value;
            window.MouseDown(tabPoint, MouseButton.Left);
            window.MouseMove(tabPoint + new Point(0, 20));
            window.MouseMove(chat);
            window.MouseUp(chat, MouseButton.Left);
            await WaitUntil(() => agent.Attachments.Count == 2);
            Assert.Equal(util, agent.Attachments[1].Path);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The providers page: FreeLLMAPI, OpenRouter and Ollama are there by default; a key typed and saved
    /// goes to the key store (never into the settings), Test Connection lists the models, and the model
    /// picker offers them; picking one is remembered in the settings.
    /// </summary>
    [AvaloniaFact]
    public async Task Agent_providers_keep_keys_out_of_settings_and_offer_their_models()
    {
        using var server = new FakeAgentServer([]);
        var keys = new MemoryApiKeyStore();
        var settings = new MemorySettingsStore();
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), settings);
        var agent = workbench.EnableAgent(new AgentServices(new HttpClient(server), keys, new RecordingCommandRunner()));
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        window.Show();
        Assert.Equal(["FreeLLMAPI", "OpenRouter", "Ollama"], agent.Providers.Select(p => p.Name));
        Assert.Equal(AiProviders.FreeLlmApiUrl, agent.Providers[0].BaseUrl);

        await workbench.Commands.ExecuteAsync(Presentation.Commands.CommandIds.AgentManageProviders);
        Dispatcher.UIThread.RunJobs();
        Assert.True(workbench.IsSecondarySideBarVisible);
        Assert.True(window.GetVisualDescendants().OfType<AgentProvidersView>().Single().IsEffectivelyVisible);
        Save(window, "agent-providers");

        var openRouter = agent.Providers.Single(p => p.Kind == AiProviderKind.OpenRouter);
        openRouter.BaseUrl = "http://fake/v1"; // the fake server instead of openrouter.ai
        var keyBox = window.GetVisualDescendants().OfType<TextBox>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "OpenRouter API key");
        keyBox.Text = "sk-or-very-secret";
        Button(window, "Save OpenRouter key").Command!.Execute(null);
        await WaitUntil(() => openRouter.Models is not null, () => $"{openRouter.StatusText} key={keys.Get("openrouter")} input={openRouter.KeyInput}");
        Assert.Equal(string.Empty, keyBox.Text);
        Assert.Equal("sk-or-very-secret", keys.Get("openrouter"));
        Assert.Equal("Connected · 2 models", openRouter.StatusText);

        var added = agent.AddProvider(AiProviderKind.OpenAiCompatible);
        Assert.Equal("OpenAI-compatible", added.Name);
        Assert.Equal(4, agent.Providers.Count);

        _ = agent.ShowModelPickerAsync();
        await WaitUntil(() => workbench.Palette.IsOpen);
        Assert.Contains(workbench.Palette.Items, i => i.Label == "Test Model Two" && i.Detail.Contains("OpenRouter", StringComparison.Ordinal));
        Assert.Contains(workbench.Palette.Items, i => i.Label.StartsWith("FreeLLMAPI: add an API key", StringComparison.Ordinal));
        await workbench.Palette.AcceptAsync(workbench.Palette.Items.Single(i => i.Label == "Test Model Two"));
        Assert.Equal("Test Model Two · OpenRouter", agent.ModelLabel);

        var saved = workbench.CurrentSettings();
        Assert.Equal("openrouter", saved.AgentProvider);
        Assert.Equal("model-2", saved.AgentModel);
        Assert.Equal(4, saved.AgentProviders!.Count);
        var json = JsonSerializer.Serialize(saved, Services.SettingsJsonContext.Default.UserSettings);
        Assert.Contains("\"agentProviders\"", json);
        Assert.Contains("http://fake/v1", json);
        Assert.DoesNotContain("sk-or-very-secret", json);
    }

    /// <summary>A provider error shows in the chat with Retry; Retry sends the same conversation again.</summary>
    [AvaloniaFact]
    public async Task Agent_errors_offer_retry()
    {
        using var server = new FakeAgentServer(
        [
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"error":{"message":"Slow down"}}""") },
            Sse("Second time lucky."),
        ]);
        var (window, _, agent) = CreateAgentWindow(server);
        window.Show();

        agent.Input = "Hi";
        await agent.SendAsync();
        var error = Assert.IsType<NoticeItem>(agent.Items[^1]);
        Assert.True(error.IsError);
        Assert.Contains("Slow down", error.Text);
        Assert.Contains("Rate limited", error.Text);
        Dispatcher.UIThread.RunJobs();
        Save(window, "agent-error");

        error.RetryCommand!.Execute(null);
        await WaitUntil(() => agent.Items.OfType<AssistantMessageItem>().Any() && !agent.IsBusy);
        Assert.DoesNotContain(agent.Items, i => i is NoticeItem);
        Assert.Equal("Second time lucky.", agent.Items.OfType<AssistantMessageItem>().Single().Text);
        Assert.Equal(server.ChatBodies[0], server.ChatBodies[1]);
    }

    // Helpers ----------------------------------------------------------------------------------------

    /// <summary>The one button with this name that is showing (decided cards keep theirs, hidden).</summary>
    private static Avalonia.Controls.Button VisibleButton(MainWindow window, string name) =>
        window.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Single(b => b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == name);

    private static (MainWindow Window, WorkbenchViewModel Workbench, AgentViewModel Agent) CreateAgentWindow(FakeAgentServer server, ICommandRunner? runner = null)
    {
        var settings = new MemorySettingsStore();
        settings.Save(new UserSettings
        {
            AgentProviders = [new AgentProviderSetting { Id = "test", Kind = "openai", Name = "Test AI", BaseUrl = "http://fake/v1" }],
            AgentProvider = "test",
            AgentModel = "test-model",
        });
        var keys = new MemoryApiKeyStore();
        keys.Set("test", "test-key");
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), settings) { IsSecondarySideBarVisible = true };
        var agent = workbench.EnableAgent(new AgentServices(new HttpClient(server), keys, runner ?? new RecordingCommandRunner()));
        return (new MainWindow(workbench) { Width = 1280, Height = 800 }, workbench, agent);
    }

    private static Func<HttpResponseMessage> Sse(params string[] pieces) => () =>
    {
        var body = new StringBuilder();
        foreach (var piece in pieces)
        {
            body.Append("data: ").Append(JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = piece } } } })).Append("\n\n");
        }

        body.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream") };
    };

    private static Func<HttpResponseMessage> Tool(string name, object arguments) => () =>
    {
        var chunk = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { delta = new { tool_calls = new[] { new { index = 0, id = "call_" + name, type = "function", function = new { name, arguments = JsonSerializer.Serialize(arguments) } } } }, finish_reason = "tool_calls" },
            },
        });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"data: {chunk}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
    };

    private static void SavePng(string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(4, 4));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(Brushes.OrangeRed, new Rect(0, 0, 4, 4));
        }

        bitmap.Save(path);
    }

    /// <summary>A 2×2, 24-bit BMP.</summary>
    private static byte[] Bmp2x2()
    {
        var pixels = new byte[] { 0, 0, 255, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0 }; // rows padded to 4 bytes
        var file = new List<byte>();
        void Int(int value) => file.AddRange(BitConverter.GetBytes(value));
        void Short(short value) => file.AddRange(BitConverter.GetBytes(value));
        file.AddRange("BM"u8.ToArray());
        Int(54 + pixels.Length);
        Int(0);
        Int(54);
        Int(40);
        Int(2);
        Int(2);
        Short(1);
        Short(24);
        Int(0);
        Int(pixels.Length);
        Int(2835);
        Int(2835);
        Int(0);
        Int(0);
        file.AddRange(pixels);
        return [.. file];
    }

    /// <summary>An OpenAI-compatible server: /models lists two models, each chat request gets the next scripted reply.</summary>
    private sealed class FakeAgentServer(IReadOnlyList<Func<HttpResponseMessage>> replies) : HttpMessageHandler
    {
        private int _next;

        public List<string> ChatBodies { get; } = [];

        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            if (request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":"model-1","name":"Test Model One"},{"id":"model-2","name":"Test Model Two","supported_parameters":["tools"]}]}""", Encoding.UTF8, "application/json"),
                };
            }

            if (request.Content is null || !request.RequestUri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"not here"}""") }; // Ollama's API, say
            }

            ChatBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return _next < replies.Count
                ? replies[_next++]()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"error":{"message":"No more scripted replies"}}""") };
        }
    }

    private sealed class RecordingCommandRunner : ICommandRunner
    {
        public List<string> Ran { get; } = [];

        public Task<CommandResult> RunAsync(string command, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Ran.Add(command);
            return Task.FromResult(new CommandResult(0, "ok", false));
        }
    }

    /// <summary>A file on disk as Windows Explorer hands it to a drop (Avalonia's own storage item; user code can't implement one).</summary>
    private static async Task<IStorageItem> LocalFile(Window window, string path) =>
        await window.StorageProvider.TryGetFileFromPathAsync(path)
        ?? (IStorageItem)Activator.CreateInstance(typeof(IStorageItem).Assembly.GetType("Avalonia.Platform.Storage.FileIO.BclStorageFile", throwOnError: true)!, new FileInfo(path))!;
}
