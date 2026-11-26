using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Coder.Core.Ai;

/// <summary>
/// OpenAI's chat completions API, which OpenRouter, FreeLLMAPI, LM Studio, vLLM, Groq and many others
/// speak: <c>POST {base}/chat/completions</c> with <c>"stream": true</c> answers in server-sent events,
/// each a JSON chunk whose <c>choices[0].delta</c> carries text, reasoning or pieces of tool calls.
/// </summary>
public sealed class OpenAiCompatibleProvider(HttpClient http, AiProviderConfig config, string? apiKey) : IChatProvider
{
    /// <summary>Who OpenRouter credits for the request (its leaderboard); names only the app.</summary>
    private const string Referer = "https://github.com/HamzaBaghdadi/coder";

    public AiProviderConfig Config => config;

    private string BaseUrl => AiProviders.NormalizeUrl(config.BaseUrl);

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        using var timeout = ProviderHttp.Timeout(ProviderHttp.ListTimeout, cancellationToken);
        try
        {
            using var request = Request(HttpMethod.Get, "/models");
            using var response = await ProviderHttp.SendAsync(http, request, config, timeout.Token).ConfigureAwait(false);
            using var document = await ProviderHttp.ReadJsonAsync(response, config, timeout.Token).ConfigureAwait(false);
            return ParseModels(document.RootElement);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException($"{config.Name} did not list its models in time ({BaseUrl}).", null, exception);
        }
    }

    /// <summary>
    /// Reads <c>{"data":[{"id":…}]}</c> (or a bare array). OpenRouter adds what each model accepts
    /// (<c>architecture.input_modalities</c>, <c>supported_parameters</c>); FreeLLMAPI says which are
    /// usable now (<c>execution_status</c>), and only those are offered when there are any.
    /// </summary>
    internal static IReadOnlyList<ModelInfo> ParseModels(JsonElement root)
    {
        var data = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var list) && list.ValueKind == JsonValueKind.Array ? list
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array ? models
            : default;
        if (data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var all = new List<(ModelInfo Model, bool Ready)>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.ValueKind == JsonValueKind.String ? item.GetString() : ProviderHttp.Str(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            bool? vision = null, tools = null;
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("architecture", out var architecture) && architecture.TryGetProperty("input_modalities", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
            {
                vision = inputs.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.String && m.GetString() == "image");
            }

            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("supported_parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
            {
                tools = parameters.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() == "tools");
            }

            var status = ProviderHttp.Str(item, "execution_status");
            all.Add((new ModelInfo(id, ProviderHttp.Str(item, "name")) { SupportsVision = vision, SupportsTools = tools, ContextLength = ProviderHttp.Int(item, "context_length") },
                status is null or "ready"));
        }

        var usable = all.Any(m => m.Ready) ? all.Where(m => m.Ready) : all;
        return usable.Select(m => m.Model)
            .OrderBy(m => m.Id.StartsWith("auto", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest chat, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, "/chat/completions");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = ProviderHttp.Json(writer => WriteBody(writer, chat));
        using var response = await ProviderHttp.SendAsync(http, request, config, cancellationToken).ConfigureAwait(false);

        // A server that ignores "stream" answers with one JSON body.
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            using var whole = await ProviderHttp.ReadJsonAsync(response, config, cancellationToken).ConfigureAwait(false);
            foreach (var item in ParseComplete(whole.RootElement))
            {
                yield return item;
            }

            yield break;
        }

        using var reader = await ProviderHttp.OpenReaderAsync(response, cancellationToken).ConfigureAwait(false);
        var state = new StreamState();
        var data = new StringBuilder();
        while (true)
        {
            var line = await ProviderHttp.ReadLineAsync(reader, config, cancellationToken).ConfigureAwait(false);
            if (line is null || line.Length == 0)
            {
                // A blank line ends an event; the end of the body ends the last one.
                if (data.Length > 0)
                {
                    var payload = data.ToString();
                    data.Clear();
                    if (payload == "[DONE]")
                    {
                        break;
                    }

                    foreach (var item in state.Read(payload, config))
                    {
                        yield return item;
                    }
                }

                if (line is null)
                {
                    break;
                }

                continue;
            }

            if (line.StartsWith(':'))
            {
                continue; // a comment, e.g. OpenRouter's ": OPENROUTER PROCESSING" keep-alive
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.AsSpan(5).TrimStart(' '));
            }
        }

        foreach (var item in state.Finish())
        {
            yield return item;
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, ProviderHttp.Address(config, BaseUrl + path));
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        if (config.Kind == AiProviderKind.OpenRouter)
        {
            request.Headers.TryAddWithoutValidation("HTTP-Referer", Referer);
            request.Headers.TryAddWithoutValidation("X-Title", "Coder");
        }

        return request;
    }

    internal static void WriteBody(Utf8JsonWriter writer, ChatRequest chat)
    {
        writer.WriteStartObject();
        writer.WriteString("model", chat.Model);
        writer.WriteBoolean("stream", true);
        if (chat.Temperature is { } temperature)
        {
            writer.WriteNumber("temperature", temperature);
        }

        writer.WriteStartArray("messages");
        foreach (var message in chat.Messages)
        {
            WriteMessage(writer, message);
        }

        writer.WriteEndArray();
        if (chat.Tools.Count > 0)
        {
            writer.WriteStartArray("tools");
            foreach (var tool in chat.Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("parameters");
                ProviderHttp.WriteSchema(writer, tool.ParametersSchema);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteMessage(Utf8JsonWriter writer, ChatMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", message.Role switch
        {
            ChatRole.System => "system",
            ChatRole.Assistant => "assistant",
            ChatRole.Tool => "tool",
            _ => "user",
        });
        if (message.Role == ChatRole.User && message.Images.Count > 0)
        {
            // Text and images as parts; models without vision reject these.
            writer.WriteStartArray("content");
            if (message.Text.Length > 0)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", message.Text);
                writer.WriteEndObject();
            }

            foreach (var image in message.Images)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "image_url");
                writer.WriteStartObject("image_url");
                writer.WriteString("url", image.ToDataUrl());
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
        else if (message.Role == ChatRole.Assistant && message.ToolCalls.Count > 0 && message.Text.Length == 0)
        {
            writer.WriteNull("content");
        }
        else
        {
            writer.WriteString("content", message.Text);
        }

        if (message.Role == ChatRole.Assistant && message.ToolCalls.Count > 0)
        {
            writer.WriteStartArray("tool_calls");
            foreach (var call in message.ToolCalls)
            {
                writer.WriteStartObject();
                writer.WriteString("id", call.Id);
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", call.Name);
                writer.WriteString("arguments", call.Arguments.Length == 0 ? "{}" : call.Arguments);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (message.Role == ChatRole.Tool)
        {
            writer.WriteString("tool_call_id", message.ToolCallId);
        }

        writer.WriteEndObject();
    }

    /// <summary>A whole (non-streamed) completion as the same events a stream gives.</summary>
    private static IEnumerable<ChatStreamEvent> ParseComplete(JsonElement root)
    {
        if (ProviderHttp.ErrorMessage(root) is { } error && root.TryGetProperty("error", out _))
        {
            throw new AiProviderException(error);
        }

        var state = new StreamState();
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message))
            {
                if ((ProviderHttp.Str(message, "reasoning_content") ?? ProviderHttp.Str(message, "reasoning")) is { Length: > 0 } reasoning)
                {
                    yield return new ReasoningDelta(reasoning);
                }

                if (ProviderHttp.Str(message, "content") is { Length: > 0 } text)
                {
                    yield return new TextDelta(text);
                }

                if (message.TryGetProperty("tool_calls", out var calls))
                {
                    state.AddToolCalls(calls);
                }
            }

            state.FinishReason = ProviderHttp.Str(choice, "finish_reason");
        }

        state.ReadUsage(root);
        foreach (var item in state.Finish())
        {
            yield return item;
        }
    }

    /// <summary>What a stream has said so far: tool calls arrive in pieces, keyed by index.</summary>
    private sealed class StreamState
    {
        private readonly SortedDictionary<int, PendingCall> _calls = [];
        private TokenUsage? _usage;

        public string? FinishReason { get; set; }

        public List<ChatStreamEvent> Read(string payload, AiProviderConfig config)
        {
            var events = new List<ChatStreamEvent>();
            using var document = ProviderHttp.TryParse(payload);
            if (document is null)
            {
                return events; // a keep-alive or a line this reader does not know
            }

            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out _))
            {
                throw new AiProviderException($"{config.Name}: {ProviderHttp.ErrorMessage(root) ?? "the reply failed."}");
            }

            ReadUsage(root);
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                return events;
            }

            var choice = choices[0];
            if (ProviderHttp.Str(choice, "finish_reason") is { } finish)
            {
                FinishReason = finish;
            }

            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            {
                return events;
            }

            if ((ProviderHttp.Str(delta, "reasoning_content") ?? ProviderHttp.Str(delta, "reasoning")) is { Length: > 0 } reasoning)
            {
                events.Add(new ReasoningDelta(reasoning));
            }

            if (ProviderHttp.Str(delta, "content") is { Length: > 0 } text)
            {
                events.Add(new TextDelta(text));
            }

            if (delta.TryGetProperty("tool_calls", out var calls))
            {
                AddToolCalls(calls);
            }

            return events;
        }

        public void AddToolCalls(JsonElement calls)
        {
            if (calls.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var position = 0;
            foreach (var call in calls.EnumerateArray())
            {
                var id = ProviderHttp.Str(call, "id");
                var index = ProviderHttp.Int(call, "index") ?? position;
                position++;
                if (id is not null && _calls.TryGetValue(index, out var taken) && taken.Id is not null && taken.Id != id)
                {
                    index = _calls.Keys.Max() + 1; // no index (Gemini): a new id is a new call
                }

                var entry = _calls.TryGetValue(index, out var existing) ? existing : new PendingCall();
                entry.Id ??= id;
                if (call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
                {
                    entry.Name ??= ProviderHttp.Str(function, "name");
                    if (function.TryGetProperty("arguments", out var arguments))
                    {
                        entry.Arguments.Append(arguments.ValueKind == JsonValueKind.String ? arguments.GetString() : arguments.GetRawText());
                    }
                }

                _calls[index] = entry;
            }
        }

        public void ReadUsage(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                _usage = new TokenUsage(ProviderHttp.Int(usage, "prompt_tokens") ?? 0, ProviderHttp.Int(usage, "completion_tokens") ?? 0);
            }
        }

        public IEnumerable<ChatStreamEvent> Finish()
        {
            var calls = _calls
                .Where(c => !string.IsNullOrEmpty(c.Value.Name))
                .Select(c => new ToolCall(c.Value.Id ?? $"call_{c.Key}", c.Value.Name!, c.Value.Arguments.ToString()))
                .ToList();
            if (calls.Count > 0)
            {
                yield return new ToolCallsRequested(calls);
            }

            yield return new ChatCompleted(FinishReason, _usage);
        }

        private sealed class PendingCall
        {
            public string? Id { get; set; }

            public string? Name { get; set; }

            public StringBuilder Arguments { get; } = new();
        }
    }
}
