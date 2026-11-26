using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Coder.Core.Ai;

/// <summary>
/// Ollama's own API (<c>http://localhost:11434</c>): <c>/api/tags</c> lists the pulled models,
/// <c>/api/show</c> says what each can do (tools, vision), and <c>/api/chat</c> streams the reply as
/// newline-delimited JSON, each line a <c>message</c> with more content and, at the end, <c>"done": true</c>.
/// </summary>
public sealed class OllamaProvider(HttpClient http, AiProviderConfig config, string? apiKey) : IChatProvider
{
    public AiProviderConfig Config => config;

    /// <summary>The server's root: a URL typed with OpenAI's "/v1" (Ollama has that API too) still works.</summary>
    private string BaseUrl
    {
        get
        {
            var url = AiProviders.NormalizeUrl(config.BaseUrl);
            return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url;
        }
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        using var timeout = ProviderHttp.Timeout(ProviderHttp.ListTimeout, cancellationToken);
        try
        {
            List<string> names;
            using (var request = Request(HttpMethod.Get, "/api/tags"))
            using (var response = await ProviderHttp.SendAsync(http, request, config, timeout.Token).ConfigureAwait(false))
            using (var document = await ProviderHttp.ReadJsonAsync(response, config, timeout.Token).ConfigureAwait(false))
            {
                names = document.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
                    ? models.EnumerateArray().Select(m => ProviderHttp.Str(m, "name") ?? ProviderHttp.Str(m, "model")).OfType<string>().ToList()
                    : [];
            }

            // What each model can do; a model /api/show can't describe is still offered.
            var details = await Task.WhenAll(names.Select(name => ShowAsync(name, timeout.Token))).ConfigureAwait(false);
            return details.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException($"{config.Name} did not list its models in time ({BaseUrl}).", null, exception);
        }
    }

    private async Task<ModelInfo> ShowAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var request = Request(HttpMethod.Post, "/api/show");
            request.Content = ProviderHttp.Json(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("model", name);
                writer.WriteEndObject();
            });
            using var response = await ProviderHttp.SendAsync(http, request, config, cancellationToken).ConfigureAwait(false);
            using var document = await ProviderHttp.ReadJsonAsync(response, config, cancellationToken).ConfigureAwait(false);
            return ParseShow(name, document.RootElement);
        }
        catch (AiProviderException)
        {
            return new ModelInfo(name);
        }
    }

    internal static ModelInfo ParseShow(string name, JsonElement root)
    {
        bool? vision = null, tools = null;
        if (root.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array)
        {
            var list = capabilities.EnumerateArray().Select(c => c.GetString()).ToList();
            vision = list.Contains("vision");
            tools = list.Contains("tools");
        }

        int? context = null;
        if (root.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in info.EnumerateObject())
            {
                if (property.Name.EndsWith(".context_length", StringComparison.Ordinal) && property.Value.TryGetInt32(out var length))
                {
                    context = length;
                }
            }
        }

        return new ModelInfo(name) { SupportsVision = vision, SupportsTools = tools, ContextLength = context };
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest chat, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, "/api/chat");
        request.Content = ProviderHttp.Json(writer => WriteBody(writer, chat));
        using var response = await ProviderHttp.SendAsync(http, request, config, cancellationToken).ConfigureAwait(false);
        using var reader = await ProviderHttp.OpenReaderAsync(response, cancellationToken).ConfigureAwait(false);

        var calls = new List<ToolCall>();
        string? reason = null;
        TokenUsage? usage = null;
        while (await ProviderHttp.ReadLineAsync(reader, config, cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = ProviderHttp.TryParse(line);
            if (document is null)
            {
                continue;
            }

            var root = document.RootElement;
            if (ProviderHttp.Str(root, "error") is { } error)
            {
                throw new AiProviderException($"{config.Name}: {error}");
            }

            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            {
                if (ProviderHttp.Str(message, "thinking") is { Length: > 0 } thinking)
                {
                    yield return new ReasoningDelta(thinking);
                }

                if (ProviderHttp.Str(message, "content") is { Length: > 0 } text)
                {
                    yield return new TextDelta(text);
                }

                if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in toolCalls.EnumerateArray())
                    {
                        if (call.TryGetProperty("function", out var function) && ProviderHttp.Str(function, "name") is { } name)
                        {
                            var arguments = function.TryGetProperty("arguments", out var args)
                                ? args.ValueKind == JsonValueKind.String ? args.GetString() ?? "{}" : args.GetRawText()
                                : "{}";
                            calls.Add(new ToolCall(ProviderHttp.Str(call, "id") ?? $"call_{calls.Count}", name, arguments));
                        }
                    }
                }
            }

            if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
            {
                reason = ProviderHttp.Str(root, "done_reason");
                if (ProviderHttp.Int(root, "prompt_eval_count") is { } prompt)
                {
                    usage = new TokenUsage(prompt, ProviderHttp.Int(root, "eval_count") ?? 0);
                }

                break;
            }
        }

        if (calls.Count > 0)
        {
            yield return new ToolCallsRequested(calls);
        }

        yield return new ChatCompleted(reason, usage);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, ProviderHttp.Address(config, BaseUrl + path));
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim()); // a proxy in front of Ollama
        }

        return request;
    }

    internal static void WriteBody(Utf8JsonWriter writer, ChatRequest chat)
    {
        writer.WriteStartObject();
        writer.WriteString("model", chat.Model);
        writer.WriteBoolean("stream", true);
        writer.WriteStartArray("messages");
        foreach (var message in chat.Messages)
        {
            writer.WriteStartObject();
            writer.WriteString("role", message.Role switch
            {
                ChatRole.System => "system",
                ChatRole.Assistant => "assistant",
                ChatRole.Tool => "tool",
                _ => "user",
            });
            writer.WriteString("content", message.Text);
            if (message.Images.Count > 0)
            {
                writer.WriteStartArray("images");
                foreach (var image in message.Images)
                {
                    writer.WriteStringValue(Convert.ToBase64String(image.Data));
                }

                writer.WriteEndArray();
            }

            if (message.ToolCalls.Count > 0)
            {
                writer.WriteStartArray("tool_calls");
                foreach (var call in message.ToolCalls)
                {
                    writer.WriteStartObject();
                    writer.WriteStartObject("function");
                    writer.WriteString("name", call.Name);
                    writer.WritePropertyName("arguments");
                    using (var arguments = ProviderHttp.TryParse(call.Arguments))
                    {
                        if (arguments is { RootElement.ValueKind: JsonValueKind.Object })
                        {
                            arguments.RootElement.WriteTo(writer);
                        }
                        else
                        {
                            writer.WriteStartObject();
                            writer.WriteEndObject();
                        }
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (message.Role == ChatRole.Tool && message.ToolName is { } toolName)
            {
                writer.WriteString("tool_name", toolName);
            }

            writer.WriteEndObject();
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

        if (chat.Temperature is { } temperature)
        {
            writer.WriteStartObject("options");
            writer.WriteNumber("temperature", temperature);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }
}
