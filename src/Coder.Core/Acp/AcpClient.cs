using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coder.Core.Lsp;

namespace Coder.Core.Acp;

/// <summary>What the editor does for an agent: show its updates, and ask the user when it asks permission.</summary>
public interface IAcpClientHost
{
    /// <summary>An update for a session (called on the connection's thread: marshal to the UI yourself).</summary>
    void OnUpdate(string sessionId, AcpUpdate update);

    /// <summary>Asks the user; returns the chosen option's id, or null when the turn was cancelled before they chose.</summary>
    Task<string?> RequestPermissionAsync(AcpPermissionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Coder's side of the Agent Client Protocol with one agent: starts the conversation (<c>initialize</c>), opens
/// sessions, sends prompts and streams the agent's updates to the <see cref="IAcpClientHost"/>. JSON-RPC, one
/// message per line on the agent's stdin and stdout. Coder offers no file system or terminal to agents yet:
/// they use their own.
/// </summary>
public sealed class AcpClient : IAsyncDisposable
{
    /// <summary>How long a cancelled turn may take to end before Coder stops waiting for the agent.</summary>
    public static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(10);

    private readonly JsonRpcConnection _connection;
    private readonly IDisposable? _process;
    private readonly Func<string>? _errorOutput;

    private AcpClient(JsonRpcConnection connection, IAcpClientHost host, IDisposable? process, Func<string>? errorOutput)
    {
        _connection = connection;
        _process = process;
        _errorOutput = errorOutput;
        connection.OnNotification("session/update", parameters =>
        {
            if (AcpJson.ParseUpdate(parameters) is var (sessionId, update))
            {
                host.OnUpdate(sessionId, update);
            }
        });
        connection.OnRequestAsync("session/request_permission", async (parameters, cancellationToken) =>
        {
            var request = AcpJson.ParsePermissionRequest(parameters) ?? throw new ArgumentException("A permission request needs a sessionId, a toolCall and options.");
            string? chosen;
            try
            {
                chosen = await host.RequestPermissionAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                chosen = null;
            }

            return new JsonObject
            {
                ["outcome"] = chosen is null
                    ? new JsonObject { ["outcome"] = "cancelled" }
                    : new JsonObject { ["outcome"] = "selected", ["optionId"] = chosen },
            };
        });
        connection.Closed += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The agent's process ended, or the connection broke.</summary>
    public event EventHandler? Exited;

    public AcpAgentInfo? Agent { get; private set; }

    /// <summary>Whether the agent takes images in prompts.</summary>
    public bool AcceptsImages { get; private set; }

    /// <summary>How the agent lets users sign in, when it needs them to.</summary>
    public IReadOnlyList<AcpAuthMethod> AuthMethods { get; private set; } = [];

    public bool IsClosed => _connection.IsClosed;

    /// <summary>Talks to an agent over streams (its stdout, its stdin); for tests and other transports.</summary>
    public static AcpClient Connect(Stream input, Stream output, IAcpClientHost host, IDisposable? owner = null)
    {
        var client = new AcpClient(new JsonRpcConnection(input, output, JsonRpcFraming.NewlineDelimited), host, owner, null);
        client._connection.Start();
        return client;
    }

    /// <summary>Starts an agent's program and connects to it.</summary>
    public static AcpClient Start(string program, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?>? environment, IAcpClientHost host)
    {
        var process = ProcessTransport.Start(program, arguments, workingDirectory, environment);
        var client = new AcpClient(new JsonRpcConnection(process.Input, process.Output, JsonRpcFraming.NewlineDelimited), host, process, () => process.RecentErrorOutput);
        client._connection.Start();
        return client;
    }

    /// <summary>Agrees on the protocol version and learns what the agent can do. Call once, first.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var version = typeof(AcpClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        var result = await CallAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = AcpProtocol.Version,
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                ["terminal"] = false,
            },
            ["clientInfo"] = new JsonObject { ["name"] = "coder", ["title"] = "Coder", ["version"] = version },
        }, cancellationToken).ConfigureAwait(false);

        var agreed = result.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : AcpProtocol.Version;
        if (agreed != AcpProtocol.Version)
        {
            throw new AcpException($"The agent speaks version {agreed} of the Agent Client Protocol; Coder speaks version {AcpProtocol.Version}.");
        }

        if (result.TryGetProperty("agentInfo", out var info) && AcpJson.String(info, "name") is { } name)
        {
            Agent = new AcpAgentInfo(name, AcpJson.String(info, "title"), AcpJson.String(info, "version"));
        }

        AcceptsImages = result.TryGetProperty("agentCapabilities", out var capabilities)
                        && capabilities.ValueKind == JsonValueKind.Object
                        && capabilities.TryGetProperty("promptCapabilities", out var prompt)
                        && prompt.ValueKind == JsonValueKind.Object
                        && prompt.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.True;
        if (result.TryGetProperty("authMethods", out var methods) && methods.ValueKind == JsonValueKind.Array)
        {
            AuthMethods = [.. methods.EnumerateArray()
                .Where(m => AcpJson.String(m, "id") is not null)
                .Select(m => new AcpAuthMethod(AcpJson.String(m, "id")!, AcpJson.String(m, "name") ?? AcpJson.String(m, "id")!, AcpJson.String(m, "description")))];
        }
    }

    /// <summary>Opens a conversation in <paramref name="workingDirectory"/> (absolute); returns its id.</summary>
    public async Task<string> NewSessionAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await CallAsync("session/new", new JsonObject { ["cwd"] = workingDirectory, ["mcpServers"] = new JsonArray() }, cancellationToken).ConfigureAwait(false);
        return AcpJson.String(result, "sessionId") ?? throw new AcpException("The agent didn't return a session id.");
    }

    /// <summary>
    /// Sends a prompt and waits for the turn to end; updates arrive meanwhile through the host. Cancelling
    /// asks the agent to stop (<c>session/cancel</c>) and waits for it to end the turn, as ACP requires, up to
    /// <see cref="CancelGrace"/>.
    /// </summary>
    public async Task<AcpStopReason> PromptAsync(string sessionId, IReadOnlyList<AcpPromptBlock> prompt, CancellationToken cancellationToken)
    {
        var blocks = new JsonArray();
        foreach (var block in prompt)
        {
            blocks.Add(block switch
            {
                AcpImageBlock image => new JsonObject { ["type"] = "image", ["mimeType"] = image.MediaType, ["data"] = Convert.ToBase64String(image.Data) },
                AcpTextBlock text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
                _ => throw new ArgumentException($"Unknown prompt block {block.GetType().Name}."),
            });
        }

        var turn = CallAsync("session/prompt", new JsonObject { ["sessionId"] = sessionId, ["prompt"] = blocks }, CancellationToken.None);
        await using (cancellationToken.Register(() => _connection.Notify("session/cancel", new JsonObject { ["sessionId"] = sessionId })))
        {
            var finished = await Task.WhenAny(turn, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            if (finished != turn)
            {
                // Cancelled: the agent should answer the prompt with "cancelled" soon.
                if (await Task.WhenAny(turn, Task.Delay(CancelGrace, CancellationToken.None)).ConfigureAwait(false) != turn)
                {
                    return new AcpStopReason("cancelled");
                }
            }
        }

        var result = await turn.ConfigureAwait(false);
        return new AcpStopReason(AcpJson.String(result, "stopReason") ?? "end_turn");
    }

    public async ValueTask DisposeAsync()
    {
        _connection.Dispose();
        _process?.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static bool SaysSignIn(string message) =>
        new[] { "auth", "logged in", "log in", "login", "sign in", "signed in" }.Any(words => message.Contains(words, StringComparison.OrdinalIgnoreCase));

    /// <summary>A request whose failures read well: the agent's message, or what it printed before it ended.</summary>
    private async Task<JsonElement> CallAsync(string method, JsonNode parameters, CancellationToken cancellationToken)
    {
        try
        {
            return await _connection.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (LanguageServerException exception)
        {
            // ACP's auth_required is -32000; agents also say it in words ("Not logged in · Please run /login").
            throw new AcpException(exception.Message, exception) { AuthRequired = exception.Code == -32000 || SaysSignIn(exception.Message) };
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            var output = _errorOutput?.Invoke().Trim();
            throw new AcpException(string.IsNullOrEmpty(output) ? "The agent stopped." : $"The agent stopped: {output}", exception);
        }
    }
}
