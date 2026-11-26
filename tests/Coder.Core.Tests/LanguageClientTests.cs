using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Coder.Core.Lsp;
using Coder.Core.Text;

namespace Coder.Core.Tests;

public sealed class LanguageClientTests : IAsyncLifetime
{
    private static readonly string FilePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "coder-lsp", "main.go"));

    private readonly FakeServer _server = new();
    private LanguageClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _client = new LanguageClient(_server.ClientTransport, Path.GetDirectoryName(FilePath)!, "fake");
        await _client.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _server.Dispose();
    }

    [Fact]
    public async Task Initialize_reads_the_server_capabilities()
    {
        Assert.Equal(LanguageClientState.Running, _client.State);
        Assert.Equal(TextSyncKind.Incremental, _client.Capabilities.TextSync);
        Assert.True(_client.Capabilities.HasCompletion);
        Assert.Equal(["."], _client.Capabilities.CompletionTriggerCharacters);
        Assert.True(_client.Capabilities.HasHover);
        Assert.True(_client.Capabilities.HasDefinition);
        Assert.Equal("initialized", (await _server.NextNotificationAsync()).Method);
    }

    [Fact]
    public async Task Document_sync_sends_the_text_and_incremental_changes()
    {
        await _server.NextNotificationAsync(); // initialized
        _client.DidOpen(FilePath, "go", 1, "package main\n");
        _client.DidChange(FilePath, 2, [new ContentChange(new TextRange(new TextPosition(0, 8), new TextPosition(0, 12)), "demo")]);

        var open = await _server.NextNotificationAsync();
        Assert.Equal("textDocument/didOpen", open.Method);
        Assert.Equal("package main\n", open.Params.GetProperty("textDocument").GetProperty("text").GetString());
        Assert.Equal(DocumentUri.FromPath(FilePath), open.Params.GetProperty("textDocument").GetProperty("uri").GetString());

        var change = await _server.NextNotificationAsync();
        Assert.Equal("textDocument/didChange", change.Method);
        var entry = change.Params.GetProperty("contentChanges")[0];
        Assert.Equal("demo", entry.GetProperty("text").GetString());
        Assert.Equal(8, entry.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(2, change.Params.GetProperty("textDocument").GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Completion_items_keep_edits_snippets_and_details()
    {
        var list = await _client.CompletionAsync(FilePath, new TextPosition(3, 6), ".", TestContext.Current.CancellationToken);

        Assert.True(list.IsIncomplete);
        var item = Assert.Single(list.Items);
        Assert.Equal("Println", item.Label);
        Assert.Equal(CompletionItemKind.Function, item.Kind);
        Assert.Equal("func(a ...any)", item.Detail);
        Assert.True(item.IsSnippet);
        Assert.Equal("Println(${1:a ...any})", item.TextEdit!.NewText);
        Assert.Equal(new TextPosition(3, 5), item.TextEdit.Range.Start);
        Assert.Equal("import \"fmt\"\n", Assert.Single(item.AdditionalTextEdits).NewText);
        Assert.Equal(".", _server.LastCompletionContext?.GetProperty("triggerCharacter").GetString());
    }

    [Fact]
    public async Task Definition_accepts_location_links_and_escaped_drive_letters()
    {
        var locations = await _client.DefinitionAsync(FilePath, new TextPosition(1, 1), TestContext.Current.CancellationToken);

        var location = Assert.Single(locations);
        Assert.Equal(FilePath, location.Path, ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal(new TextPosition(7, 5), location.Range.Start);
    }

    [Fact]
    public async Task Hover_reads_markup_content()
    {
        var hover = await _client.HoverAsync(FilePath, new TextPosition(0, 0), TestContext.Current.CancellationToken);

        Assert.NotNull(hover);
        Assert.True(hover.IsMarkdown);
        Assert.Contains("func main()", hover.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Published_diagnostics_reach_the_client()
    {
        var received = new TaskCompletionSource<DiagnosticsReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.DiagnosticsPublished += (_, report) => received.TrySetResult(report);

        _server.Connection.Notify("textDocument/publishDiagnostics", new JsonObject
        {
            ["uri"] = DocumentUri.FromPath(FilePath),
            ["diagnostics"] = new JsonArray(new JsonObject
            {
                ["range"] = RangeJson(2, 1, 2, 4),
                ["severity"] = 2,
                ["message"] = "unused variable",
                ["source"] = "compiler",
            }),
        });

        var report = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("unused variable", diagnostic.Message);
        Assert.Equal(new TextPosition(2, 4), diagnostic.Range.End);
    }

    [Fact]
    public async Task Server_requests_get_default_answers()
    {
        var result = await _server.Connection.RequestAsync("workspace/configuration",
            new JsonObject { ["items"] = new JsonArray(new JsonObject(), new JsonObject()) }, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.GetArrayLength());
        Assert.All(result.EnumerateArray(), item => Assert.Equal(JsonValueKind.Null, item.ValueKind));
    }

    [Fact]
    public async Task Cancelling_a_request_tells_the_other_side()
    {
        // A peer that never answers: read what the connection writes straight from the pipe.
        var toPeer = new MemoryPipe();
        using var connection = new JsonRpcConnection(new MemoryPipe(), toPeer);
        connection.Start();
        using var cancel = new CancellationTokenSource();

        var request = connection.RequestAsync("textDocument/hover", new JsonObject(), cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        var written = new List<byte>();
        var buffer = new byte[4096];
        while (!System.Text.Encoding.UTF8.GetString(written.ToArray()).Contains("$/cancelRequest", StringComparison.Ordinal))
        {
            var read = await toPeer.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            written.AddRange(buffer.AsSpan(0, read).ToArray());
        }
    }


    private static JsonObject RangeJson(int startLine, int startColumn, int endLine, int endColumn) => new()
    {
        ["start"] = new JsonObject { ["line"] = startLine, ["character"] = startColumn },
        ["end"] = new JsonObject { ["line"] = endLine, ["character"] = endColumn },
    };

    /// <summary>A scripted language server on the other end of in-memory pipes.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly Channel<(string Method, JsonElement Params)> _notifications = Channel.CreateUnbounded<(string, JsonElement)>();

        public FakeServer()
        {
            var toServer = new MemoryPipe();
            var toClient = new MemoryPipe();
            ClientTransport = new Transport(toClient, toServer);
            Connection = new JsonRpcConnection(toServer, toClient);

            Connection.OnRequest("initialize", _ => JsonNode.Parse("""
                {"capabilities":{"textDocumentSync":{"openClose":true,"change":2},
                 "completionProvider":{"triggerCharacters":["."]},"hoverProvider":true,"definitionProvider":{}}}
                """));
            Connection.OnRequest("shutdown", _ => null);
            Connection.OnRequest("textDocument/completion", p =>
            {
                LastCompletionContext = p.GetProperty("context").Clone();
                return JsonNode.Parse("""
                    {"isIncomplete":true,"items":[{"label":"Println","kind":3,"detail":"func(a ...any)","insertTextFormat":2,
                     "textEdit":{"range":{"start":{"line":3,"character":5},"end":{"line":3,"character":6}},"newText":"Println(${1:a ...any})"},
                     "additionalTextEdits":[{"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":0}},"newText":"import \"fmt\"\n"}]}]}
                    """);
            });
            Connection.OnRequest("textDocument/definition", _ =>
            {
                // Like gopls on Windows: lower-case drive with an escaped colon.
                var uri = DocumentUri.FromPath(FilePath);
                if (OperatingSystem.IsWindows())
                {
                    uri = "file:///" + char.ToLowerInvariant(FilePath[0]) + "%3A" + uri[(uri.IndexOf(':', 8) + 1)..];
                }

                return new JsonArray(new JsonObject
                {
                    ["targetUri"] = uri,
                    ["targetRange"] = RangeJson(7, 0, 9, 1),
                    ["targetSelectionRange"] = RangeJson(7, 5, 7, 9),
                });
            });
            Connection.OnRequest("textDocument/hover", _ => JsonNode.Parse("""{"contents":{"kind":"markdown","value":"```go\nfunc main()\n```"}}"""));

            foreach (var method in new[] { "initialized", "textDocument/didOpen", "textDocument/didChange", "$/cancelRequest", "exit" })
            {
                Connection.OnNotification(method, p => _notifications.Writer.TryWrite((method, p)));
            }

            Connection.Start();
        }

        public ILanguageServerTransport ClientTransport { get; }

        public JsonRpcConnection Connection { get; }

        public JsonElement? LastCompletionContext { get; private set; }

        public async Task<(string Method, JsonElement Params)> NextNotificationAsync() =>
            await _notifications.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public void Dispose() => Connection.Dispose();

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
}
