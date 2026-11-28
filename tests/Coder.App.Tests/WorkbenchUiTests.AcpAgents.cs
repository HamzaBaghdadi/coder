using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Workbench;
using Coder.Core.Acp;
using Coder.Core.Ai;
using Coder.Core.Files;
using Coder.Core.Lsp;
using Coder.Presentation.Agent;
using Coder.Presentation.Settings;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// A coding agent from an extension (Agent Client Protocol) in Coder Agent: it's offered in the picker,
    /// asks consent once before starting, streams its reply into the chat, shows its tool call with the
    /// file change, asks permission with its own choices (the user allows), and Stop ends a turn that waits.
    /// </summary>
    [AvaloniaFact]
    public async Task Agents_over_acp_stream_into_the_chat_and_ask_permission_with_their_choices()
    {
        var folder = Directory.CreateTempSubdirectory("coder-acp-ui-").FullName;
        var file = Path.Combine(folder, "a.txt");
        await File.WriteAllTextAsync(file, "old\n", TestContext.Current.CancellationToken);
        using var fake = new ScriptedAcpAgent(file);
        var allowed = 0;
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore()) { IsSecondarySideBarVisible = true };
        workbench.AgentServers.Add(new AgentServer("acme.agent", "Acme Agent", "acme.exe", ["--acp"], "Acme Tools")
        {
            AllowAsync = () =>
            {
                allowed++;
                return Task.FromResult(true);
            },
        });
        var started = new List<(string Program, string Folder)>();
        var agent = workbench.EnableAgent(new AgentServices(new HttpClient(), new MemoryApiKeyStore(), new RecordingCommandRunner())
        {
            StartAgent = (server, cwd, host) =>
            {
                started.Add((server.Program, cwd));
                return AcpClient.Connect(fake.ClientInput, fake.ClientOutput, host);
            },
        });
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            agent.SelectModel("agent:acme.agent", "acme.agent");
            Assert.Equal("Acme Agent · Agent", agent.ModelLabel);

            // A turn: text, then a tool call that changes a file and asks permission.
            agent.Input = "Fix a.txt";
            var turn = agent.SendAsync();
            await WaitUntil(() => agent.Items.OfType<ToolCallItem>().FirstOrDefault()?.Proposal is PermissionProposalItem, () => $"items: {string.Join(", ", agent.Items.Select(i => i.GetType().Name))}");
            Assert.Equal([("acme.exe", folder)], started);
            Assert.Equal(1, allowed);
            Assert.Equal(folder, fake.SessionFolder);
            Assert.Contains("Fix a.txt", fake.LastPrompt, StringComparison.Ordinal);
            Assert.Equal("Let me look.", agent.Items.OfType<AssistantMessageItem>().First().Text);
            var tool = agent.Items.OfType<ToolCallItem>().Single();
            Assert.Equal(("Edit a.txt", ToolStatus.WaitingForUser), (tool.Title, tool.Status));
            var card = (PermissionProposalItem)tool.Proposal!;
            Assert.Equal(["Allow once", "Always allow", "Reject"], card.Choices.Select(c => c.Name));
            Assert.True(card.Choices[0].IsPrimary);
            Assert.Equal(("a.txt", 1, 1), (card.RelativePath, card.Added, card.Removed));
            Dispatcher.UIThread.RunJobs();
            Assert.Single(window.GetVisualDescendants().OfType<Control>(), c => c.Classes.Contains("permission-proposal"));
            Save(window, "agent-acp-permission");

            VisibleButton(window, "Allow once").Command!.Execute(null);
            await turn.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal("selected:allow", fake.PermissionOutcome);
            Assert.Equal((ToolStatus.Succeeded, "Edited."), (tool.Status, tool.Result));
            Assert.Equal("All fixed.", agent.Items.OfType<AssistantMessageItem>().Last().Text);
            Assert.False(agent.IsBusy);

            // Stop while the agent waits for permission: the card is cancelled and the agent hears session/cancel.
            fake.WaitForCancel = true;
            agent.Input = "Again";
            var second = agent.SendAsync();
            await WaitUntil(() => agent.Items.OfType<ToolCallItem>().Count() == 2 && agent.Items.OfType<ToolCallItem>().Last().Proposal is PermissionProposalItem);
            agent.Stop();
            await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(ProposalState.Cancelled, ((PermissionProposalItem)agent.Items.OfType<ToolCallItem>().Last().Proposal!).State);
            Assert.Equal("cancelled:", fake.PermissionOutcome);
            Assert.True(fake.Cancelled);
            Assert.Equal("Stopped.", agent.Items.OfType<NoticeItem>().Last().Text);
            Assert.Single(started); // one process for both turns, one session
            Assert.Equal(1, fake.Sessions);

            // New Chat opens a new session with the same agent.
            agent.NewChat();
            fake.WaitForCancel = false;
            agent.Input = "Third";
            var third = agent.SendAsync();
            await WaitUntil(() => agent.Items.OfType<ToolCallItem>().FirstOrDefault()?.Proposal is PermissionProposalItem { IsPending: true });
            ((PermissionProposalItem)agent.Items.OfType<ToolCallItem>().First().Proposal!).Choose("reject");
            await third.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(2, fake.Sessions);
            Assert.Equal("selected:reject", fake.PermissionOutcome);
            Assert.Equal((ToolStatus.Failed, "Not allowed (Reject)."), (agent.Items.OfType<ToolCallItem>().First().Status, agent.Items.OfType<ToolCallItem>().First().Result));
        }
        finally
        {
            window.Close();
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>An agent that can't start (or refuses) shows why in the chat instead of failing silently.</summary>
    [AvaloniaFact]
    public async Task An_agent_the_user_didnt_allow_explains_itself()
    {
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore()) { IsSecondarySideBarVisible = true };
        workbench.AgentServers.Add(new AgentServer("acme.agent", "Acme Agent", "acme.exe", [], "Acme Tools") { AllowAsync = () => Task.FromResult(false) });
        var agent = workbench.EnableAgent(new AgentServices(new HttpClient(), new MemoryApiKeyStore(), new RecordingCommandRunner())
        {
            StartAgent = (_, _, _) => throw new InvalidOperationException("must not start"),
        });
        agent.SelectModel("agent:acme.agent", "acme.agent");
        agent.Input = "hi";
        await agent.SendAsync();
        var notice = agent.Items.OfType<NoticeItem>().Single();
        Assert.True(notice.IsError);
        Assert.Contains("isn't allowed to run", notice.Text, StringComparison.Ordinal);

        // Uninstalled while picked: the label says so and sending asks for another.
        workbench.AgentServers.Clear();
        Assert.Equal("Pick a model", agent.ModelLabel);
    }

    /// <summary>A scripted ACP agent on in-process pipes.</summary>
    private sealed class ScriptedAcpAgent : IDisposable
    {
        private readonly Pipe _toClient = new();
        private readonly Pipe _fromClient = new();
        private readonly JsonRpcConnection _connection;
        private int _turn;

        public ScriptedAcpAgent(string file)
        {
            ClientInput = _toClient;
            ClientOutput = _fromClient;
            _connection = new JsonRpcConnection(_fromClient, _toClient, JsonRpcFraming.NewlineDelimited);
            _connection.OnRequest("initialize", _ => JsonNode.Parse("""{"protocolVersion":1,"agentInfo":{"name":"acme","title":"Acme Agent"}}"""));
            _connection.OnRequest("session/new", p =>
            {
                SessionFolder = p.GetProperty("cwd").GetString();
                Sessions++;
                return new JsonObject { ["sessionId"] = $"s{Sessions}" };
            });
            _connection.OnNotification("session/cancel", _ => Cancelled = true);
            _connection.OnRequestAsync("session/prompt", async (p, token) =>
            {
                var session = p.GetProperty("sessionId").GetString()!;
                LastPrompt = p.GetProperty("prompt")[0].GetProperty("text").GetString();
                var id = $"t{++_turn}";
                Update(session, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Let me look."}}""");
                var call = JsonNode.Parse("""{"sessionUpdate":"tool_call","title":"Edit a.txt","kind":"edit","status":"pending"}""")!;
                call["toolCallId"] = id;
                call["content"] = new JsonArray(new JsonObject { ["type"] = "diff", ["path"] = file, ["oldText"] = "old\n", ["newText"] = "new\n" });
                _connection.Notify("session/update", new JsonObject { ["sessionId"] = session, ["update"] = call });

                var answer = await _connection.RequestAsync("session/request_permission", new JsonObject
                {
                    ["sessionId"] = session,
                    ["toolCall"] = new JsonObject { ["toolCallId"] = id },
                    ["options"] = JsonNode.Parse("""
                        [{"optionId":"allow","name":"Allow once","kind":"allow_once"},
                         {"optionId":"always","name":"Always allow","kind":"allow_always"},
                         {"optionId":"reject","name":"Reject","kind":"reject_once"}]
                        """),
                }, token);
                var outcome = answer.GetProperty("outcome");
                PermissionOutcome = $"{outcome.GetProperty("outcome").GetString()}:{(outcome.TryGetProperty("optionId", out var chosen) ? chosen.GetString() : null)}";
                if (WaitForCancel || outcome.GetProperty("outcome").GetString() == "cancelled")
                {
                    return new JsonObject { ["stopReason"] = "cancelled" };
                }

                if (chosen.ValueKind == JsonValueKind.String && chosen.GetString() == "reject")
                {
                    Update(session, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Okay, I won't."}}""");
                    return new JsonObject { ["stopReason"] = "end_turn" };
                }

                var done = JsonNode.Parse("""{"sessionUpdate":"tool_call_update","status":"completed","content":[{"type":"content","content":{"type":"text","text":"Edited."}}]}""")!;
                done["toolCallId"] = id;
                _connection.Notify("session/update", new JsonObject { ["sessionId"] = session, ["update"] = done });
                Update(session, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"All fixed."}}""");
                return new JsonObject { ["stopReason"] = "end_turn" };
            });
            _connection.Start();
        }

        public Stream ClientInput { get; }

        public Stream ClientOutput { get; }

        public string? SessionFolder { get; private set; }

        public int Sessions { get; private set; }

        public string? LastPrompt { get; private set; }

        public string? PermissionOutcome { get; private set; }

        public bool WaitForCancel { get; set; }

        public bool Cancelled { get; private set; }

        public void Dispose()
        {
            _connection.Dispose();
            ClientInput.Dispose();
            ClientOutput.Dispose();
            _toClient.Dispose();
            _fromClient.Dispose();
        }

        private void Update(string session, string update) =>
            _connection.Notify("session/update", new JsonObject { ["sessionId"] = session, ["update"] = JsonNode.Parse(update) });
    }
}
