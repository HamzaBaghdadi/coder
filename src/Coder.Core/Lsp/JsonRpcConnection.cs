using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Coder.Core.Lsp;

/// <summary>
/// JSON-RPC 2.0 over a byte stream with <c>Content-Length</c> framing, as the Language Server
/// Protocol uses on stdio. Writes go through one queue so messages never interleave and callers
/// never block; reads run on a background loop.
/// <para>Handlers and the <see cref="Closed"/> event run on the read loop's thread: callers that touch
/// UI state must marshal themselves.</para>
/// </summary>
public sealed class JsonRpcConnection : IDisposable
{
    private const int MethodNotFound = -32601;
    private const int InternalError = -32603;
    private const int RequestCancelled = -32800;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Dictionary<string, Action<JsonElement>> _notificationHandlers = [];
    private readonly Dictionary<string, Func<JsonElement, JsonNode?>> _requestHandlers = [];
    private readonly CancellationTokenSource _lifetime = new();
    private int _nextId;
    private int _closed;

    /// <param name="input">Stream the other side writes to (its stdout).</param>
    /// <param name="output">Stream the other side reads from (its stdin).</param>
    public JsonRpcConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>Raised once when the connection ends; the argument is the error, or null for a clean end of stream.</summary>
    public event EventHandler<Exception?>? Closed;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Registers a handler for notifications the other side sends. Call before <see cref="Start"/>.</summary>
    public void OnNotification(string method, Action<JsonElement> handler) => _notificationHandlers[method] = handler;

    /// <summary>Registers a handler for requests the other side sends; its return value is the result.</summary>
    public void OnRequest(string method, Func<JsonElement, JsonNode?> handler) => _requestHandlers[method] = handler;

    public void Start()
    {
        _ = Task.Run(WriteLoopAsync);
        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>Sends a request and waits for its result. Cancelling also tells the other side to stop.</summary>
    public async Task<JsonElement> RequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        Send(message);
        await using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var cancelled))
            {
                Notify("$/cancelRequest", new JsonObject { ["id"] = id });
                cancelled.TrySetCanceled(cancellationToken);
            }
        });

        return await completion.Task.ConfigureAwait(false);
    }

    public void Notify(string method, JsonNode? parameters)
    {
        if (IsClosed)
        {
            return;
        }

        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        Send(message);
    }

    public void Dispose() => Close(null);

    private void Send(JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var frame = new byte[header.Length + body.Length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame, header.Length);
        _outgoing.Writer.TryWrite(frame);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var frame in _outgoing.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                await _output.WriteAsync(frame, _lifetime.Token).ConfigureAwait(false);
                await _output.FlushAsync(_lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Close(exception);
        }
    }

    private async Task ReadLoopAsync()
    {
        var reader = new FrameReader(_input);
        try
        {
            while (await reader.ReadAsync(_lifetime.Token).ConfigureAwait(false) is { } body)
            {
                using var document = JsonDocument.Parse(body);
                Dispatch(document.RootElement);
            }

            Close(null);
        }
        catch (OperationCanceledException)
        {
            Close(null);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or JsonException or FormatException)
        {
            Close(exception);
        }
    }

    private void Dispatch(JsonElement message)
    {
        var hasMethod = message.TryGetProperty("method", out var methodElement);
        var hasId = message.TryGetProperty("id", out var id);
        var parameters = message.TryGetProperty("params", out var p) ? p.Clone() : default;

        if (hasMethod && hasId)
        {
            HandleRequest(id.Clone(), methodElement.GetString() ?? string.Empty, parameters);
        }
        else if (hasMethod)
        {
            if (_notificationHandlers.TryGetValue(methodElement.GetString() ?? string.Empty, out var handler))
            {
                handler(parameters);
            }
        }
        else if (hasId && id.ValueKind == JsonValueKind.Number && _pending.TryRemove(id.GetInt32(), out var completion))
        {
            if (message.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                var text = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                completion.TrySetException(code == RequestCancelled
                    ? new OperationCanceledException(text)
                    : new LanguageServerException(code, text ?? "Unknown error"));
            }
            else
            {
                completion.TrySetResult(message.TryGetProperty("result", out var result) ? result.Clone() : default);
            }
        }
    }

    private void HandleRequest(JsonElement id, string method, JsonElement parameters)
    {
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()) };
        if (!_requestHandlers.TryGetValue(method, out var handler))
        {
            response["error"] = new JsonObject { ["code"] = MethodNotFound, ["message"] = $"Unhandled method {method}" };
        }
        else
        {
            try
            {
                response["result"] = handler(parameters);
            }
            catch (Exception exception)
            {
                response["error"] = new JsonObject { ["code"] = InternalError, ["message"] = exception.Message };
            }
        }

        Send(response);
    }

    private void Close(Exception? error)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _outgoing.Writer.TryComplete();
        _lifetime.Cancel();
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var completion))
            {
                completion.TrySetException(new IOException("The language server connection closed.", error));
            }
        }

        Closed?.Invoke(this, error);
    }

    /// <summary>Reads <c>Content-Length</c> framed message bodies.</summary>
    private sealed class FrameReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _start;
        private int _end;

        /// <summary>The next message body, or null at end of stream.</summary>
        public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            var contentLength = -1;
            while (true)
            {
                var line = await ReadHeaderLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return null;
                }

                if (line.Length == 0)
                {
                    break; // end of headers
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(line[(colon + 1)..].Trim(), CultureInfo.InvariantCulture);
                }
            }

            if (contentLength < 0)
            {
                throw new FormatException("Message without Content-Length header.");
            }

            var body = new byte[contentLength];
            var copied = 0;
            while (copied < contentLength)
            {
                if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                var count = Math.Min(contentLength - copied, _end - _start);
                Array.Copy(_buffer, _start, body, copied, count);
                _start += count;
                copied += count;
            }

            return body;
        }

        private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                var b = _buffer[_start++];
                if (b == '\n')
                {
                    return line.ToString().TrimEnd('\r');
                }

                line.Append((char)b);
            }
        }

        private async Task<bool> FillAsync(CancellationToken cancellationToken)
        {
            _start = 0;
            _end = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            return _end > 0;
        }
    }
}

/// <summary>An error response from a language server.</summary>
public sealed class LanguageServerException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
