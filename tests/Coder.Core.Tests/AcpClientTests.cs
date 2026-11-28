using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coder.Core.Acp;
using Coder.Core.Lsp;

namespace Coder.Core.Tests;

/// <summary>Coder's Agent Client Protocol client against a scripted agent on in-process pipes.</summary>
public sealed class AcpClientTests : IAsyncDisposable
{
    private readonly FakeAgent _agent = new();
    private readonly RecordingHost _host = new();
    private readonly AcpClient _client;

    public AcpClientTests()
    {
        _client = AcpClient.Connect(_agent.ClientInput, _agent.ClientOutput, _host);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _agent.Dispose();
    }

    [Fact]
    public async Task Initialize_agrees_on_version_1_and_reads_what_the_agent_can_do()
    {
        await _client.InitializeAsync(TestContext.Current.CancellationToken);

        var sent = _agent.Received("initialize");
        Assert.Equal(1, sent.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("coder", sent.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.False(sent.GetProperty("clientCapabilities").GetProperty("fs").GetProperty("readTextFile").GetBoolean());
        Assert.Equal("Fake Agent", _client.Agent?.DisplayName);
        Assert.True(_client.AcceptsImages);
        Assert.Equal("login", Assert.Single(_client.AuthMethods).Id);
    }

    [Fact]
    public async Task An_agent_on_another_protocol_version_is_refused()
    {
        _agent.ProtocolVersion = 2;
        var error = await Assert.ThrowsAsync<AcpException>(() => _client.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Contains("version 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_prompt_streams_updates_asks_permission_and_ends_with_the_stop_reason()
    {
        await _client.InitializeAsync(TestContext.Current.CancellationToken);
        var session = await _client.NewSessionAsync(@"C:\work", TestContext.Current.CancellationToken);
        Assert.Equal("sess-1", session);
        Assert.Equal(@"C:\work", _agent.Received("session/new").GetProperty("cwd").GetString());
        _host.Answer = "allow";

        var stop = await _client.PromptAsync(session, [new AcpTextBlock("fix it"), new AcpImageBlock("image/png", [1, 2, 3])], TestContext.Current.CancellationToken);

        Assert.Equal("end_turn", stop.Value);
        var prompt = _agent.Received("session/prompt").GetProperty("prompt");
        Assert.Equal(("text", "fix it"), (prompt[0].GetProperty("type").GetString(), prompt[0].GetProperty("text").GetString()));
        Assert.Equal(("image", "image/png", "AQID"), (prompt[1].GetProperty("type").GetString(), prompt[1].GetProperty("mimeType").GetString(), prompt[1].GetProperty("data").GetString()));

        // Updates arrive in order: text, reasoning, the tool call with its diff, then its completion.
        Assert.Equal("Looking", Assert.IsType<AcpMessageChunk>(_host.Updates[0]).Text);
        Assert.Equal("hmm", Assert.IsType<AcpThoughtChunk>(_host.Updates[1]).Text);
        var call = Assert.IsType<AcpToolCall>(_host.Updates[2]);
        Assert.Equal(("t1", "Edit a.txt", "edit", "pending", false), (call.Id, call.Title, call.Kind, call.Status, call.IsUpdate));
        var diff = Assert.IsType<AcpDiffContent>(Assert.Single(call.Content!));
        Assert.Equal((@"C:\work\a.txt", "old", "new"), (diff.Path, diff.OldText, diff.NewText));
        var done = Assert.IsType<AcpToolCall>(_host.Updates[3]);
        Assert.Equal(("t1", "completed", true, (string?)null), (done.Id, done.Status, done.IsUpdate, done.Title));
        Assert.Equal("Done", Assert.IsType<AcpTextContent>(Assert.Single(done.Content!)).Text);

        // The permission request reached the host with the agent's options, and the answer went back.
        var asked = Assert.Single(_host.Permissions);
        Assert.Equal(["allow", "always", "reject"], asked.Options.Select(o => o.Id));
        Assert.True(asked.Options[0].Allows);
        Assert.False(asked.Options[2].Allows);
        Assert.Equal("selected:allow", _agent.PermissionOutcome);
    }

    [Fact]
    public async Task Cancelling_a_prompt_sends_session_cancel_and_waits_for_the_agent_to_end_the_turn()
    {
        await _client.InitializeAsync(TestContext.Current.CancellationToken);
        var session = await _client.NewSessionAsync(@"C:\work", TestContext.Current.CancellationToken);
        _agent.HangUntilCancelled = true;
        using var stop = new CancellationTokenSource();

        var turn = _client.PromptAsync(session, [new AcpTextBlock("long job")], stop.Token);
        await _agent.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        stop.Cancel();

        Assert.True((await turn.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).IsCancelled);
        Assert.Equal("sess-1", _agent.Received("session/cancel").GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task An_agent_error_reads_as_an_AcpException_and_auth_required_is_flagged()
    {
        await _client.InitializeAsync(TestContext.Current.CancellationToken);
        _agent.FailNewSession = true;
        var error = await Assert.ThrowsAsync<AcpException>(() => _client.NewSessionAsync(@"C:\work", TestContext.Current.CancellationToken));
        Assert.True(error.AuthRequired);
        Assert.Contains("Not logged in", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Updates_Coder_doesnt_show_and_malformed_ones_are_skipped()
    {
        Assert.Null(AcpJson.ParseUpdate(JsonDocument.Parse("""{"sessionId":"s","update":{"sessionUpdate":"available_commands_update"}}""").RootElement));
        Assert.Null(AcpJson.ParseUpdate(JsonDocument.Parse("""{"update":{"sessionUpdate":"agent_message_chunk"}}""").RootElement));
        var plan = AcpJson.ParseUpdate(JsonDocument.Parse("""{"sessionId":"s","update":{"sessionUpdate":"plan","entries":[{"content":"Read","priority":"high","status":"completed"},{"content":"Fix","status":"pending"}]}}""").RootElement);
        Assert.Equal([("Read", "completed"), ("Fix", "pending")], Assert.IsType<AcpPlan>(plan!.Value.Update).Entries);
    }

    /// <summary>Messages split across reads, CRLF endings and blank lines still arrive one by one.</summary>
    [Fact]
    public async Task Newline_framing_reads_messages_split_across_reads()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"a\",\"params\":{\"t\":\"é\"}}\r\n\n{\"jsonrpc\":\"2.0\",\"method\":\"b\"}\n");
        using var input = new TrickleStream(bytes, chunk: 3);
        using var connection = new JsonRpcConnection(input, Stream.Null, JsonRpcFraming.NewlineDelimited);
        var seen = new List<string>();
        var both = new TaskCompletionSource();
        connection.OnNotification("a", p => seen.Add("a:" + p.GetProperty("t").GetString()));
        connection.OnNotification("b", _ =>
        {
            seen.Add("b");
            both.TrySetResult();
        });
        connection.Start();
        await both.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["a:é", "b"], seen);
    }

    private sealed class RecordingHost : IAcpClientHost
    {
        public List<AcpUpdate> Updates { get; } = [];

        public List<AcpPermissionRequest> Permissions { get; } = [];

        public string? Answer { get; set; }

        public void OnUpdate(string sessionId, AcpUpdate update)
        {
            lock (Updates)
            {
                Updates.Add(update);
            }
        }

        public Task<string?> RequestPermissionAsync(AcpPermissionRequest request, CancellationToken cancellationToken)
        {
            Permissions.Add(request);
            return Task.FromResult(Answer);
        }
    }

    /// <summary>A scripted ACP agent: answers initialize, session/new and session/prompt like a real one would.</summary>
    private sealed class FakeAgent : IDisposable
    {
        private readonly AnonymousPipeServerStream _toClient = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _fromClient = new(PipeDirection.In);
        private readonly JsonRpcConnection _connection;
        private readonly Dictionary<string, JsonElement> _received = [];
        private TaskCompletionSource? _cancelled;

        public FakeAgent()
        {
            ClientInput = new AnonymousPipeClientStream(PipeDirection.In, _toClient.ClientSafePipeHandle);
            ClientOutput = new AnonymousPipeClientStream(PipeDirection.Out, _fromClient.ClientSafePipeHandle);
            _connection = new JsonRpcConnection(_fromClient, _toClient, JsonRpcFraming.NewlineDelimited);
            _connection.OnRequest("initialize", p =>
            {
                Record("initialize", p);
                var result = JsonNode.Parse("""
                    {"agentCapabilities":{"promptCapabilities":{"image":true}},
                     "agentInfo":{"name":"fake","title":"Fake Agent","version":"1.0"},"authMethods":[{"id":"login","name":"Log in"}]}
                    """)!;
                result["protocolVersion"] = ProtocolVersion;
                return result;
            });
            _connection.OnRequest("session/new", p =>
            {
                Record("session/new", p);
                return FailNewSession ? throw new InvalidOperationException("Not logged in · Please run /login") : new JsonObject { ["sessionId"] = "sess-1" };
            });
            _connection.OnNotification("session/cancel", p =>
            {
                Record("session/cancel", p);
                _cancelled?.TrySetResult();
            });
            _connection.OnRequestAsync("session/prompt", async (p, token) =>
            {
                Record("session/prompt", p);
                PromptStarted.TrySetResult();
                if (HangUntilCancelled)
                {
                    _cancelled = new TaskCompletionSource();
                    await _cancelled.Task;
                    return new JsonObject { ["stopReason"] = "cancelled" };
                }

                Update("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Looking"}}""");
                Update("""{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"hmm"}}""");
                Update("""{"sessionUpdate":"tool_call","toolCallId":"t1","title":"Edit a.txt","kind":"edit","status":"pending","content":[{"type":"diff","path":"C:\\work\\a.txt","oldText":"old","newText":"new"}]}""");
                var answer = await _connection.RequestAsync("session/request_permission", JsonNode.Parse("""
                    {"sessionId":"sess-1","toolCall":{"toolCallId":"t1"},"options":[
                      {"optionId":"allow","name":"Allow once","kind":"allow_once"},
                      {"optionId":"always","name":"Always allow","kind":"allow_always"},
                      {"optionId":"reject","name":"Reject","kind":"reject_once"}]}
                    """), token);
                var outcome = answer.GetProperty("outcome");
                PermissionOutcome = $"{outcome.GetProperty("outcome").GetString()}:{(outcome.TryGetProperty("optionId", out var id) ? id.GetString() : null)}";
                Update("""{"sessionUpdate":"tool_call_update","toolCallId":"t1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"Done"}}]}""");
                return new JsonObject { ["stopReason"] = "end_turn" };
            });
            _connection.Start();
        }

        public Stream ClientInput { get; }

        public Stream ClientOutput { get; }

        public int ProtocolVersion { get; set; } = 1;

        public bool FailNewSession { get; set; }

        public bool HangUntilCancelled { get; set; }

        public string? PermissionOutcome { get; private set; }

        public TaskCompletionSource PromptStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JsonElement Received(string method)
        {
            lock (_received)
            {
                return _received[method];
            }
        }

        public void Dispose()
        {
            _connection.Dispose();
            ClientInput.Dispose();
            ClientOutput.Dispose();
            _toClient.Dispose();
            _fromClient.Dispose();
        }

        private void Record(string method, JsonElement parameters)
        {
            lock (_received)
            {
                _received[method] = parameters.Clone();
            }
        }

        private void Update(string update) =>
            _connection.Notify("session/update", new JsonObject { ["sessionId"] = "sess-1", ["update"] = JsonNode.Parse(update) });
    }

    /// <summary>Gives its bytes a few at a time, like a pipe under load.</summary>
    private sealed class TrickleStream(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }
}
