using System.Net;
using System.Text;
using System.Text.Json;
using Coder.Core.Ai;

namespace Coder.Core.Tests;

/// <summary>Answers HTTP requests from a script and remembers what was asked, so providers run without a network.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public static HttpResponseMessage Sse(params string[] events)
    {
        var body = string.Concat(events.Select(e => e.StartsWith(':') ? e + "\n\n" : $"data: {e}\n\n"));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Ndjson(params string[] lines) =>
        new(HttpStatusCode.OK) { Content = new StringContent(string.Join("\n", lines) + "\n", Encoding.UTF8, "application/x-ndjson") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return respond(request, body);
    }
}

public sealed class AgentProviderTests
{
    private static readonly AiProviderConfig OpenRouter = new("openrouter", AiProviderKind.OpenRouter, "OpenRouter", "https://openrouter.ai/api/v1/");
    private static readonly AiProviderConfig FreeLlm = new("freellmapi", AiProviderKind.FreeLlmApi, "FreeLLMAPI", AiProviders.FreeLlmApiUrl);
    private static readonly AiProviderConfig Ollama = new("ollama", AiProviderKind.Ollama, "Ollama", AiProviders.OllamaUrl);

    private static async Task<List<ChatStreamEvent>> Collect(IChatProvider provider, ChatRequest request)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var item in provider.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        return events;
    }

    [Fact]
    public async Task OpenAi_stream_yields_text_reasoning_and_usage_and_sends_the_conversation()
    {
        using var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Sse(
            ": OPENROUTER PROCESSING",
            """{"choices":[{"delta":{"role":"assistant","reasoning":"Thinking it over."}}]}""",
            """{"choices":[{"delta":{"content":"Hello"}}]}""",
            """{"choices":[{"delta":{"content":", world"},"finish_reason":"stop"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":12,"completion_tokens":3}}""",
            "[DONE]"));
        using var http = new HttpClient(handler);
        var provider = AiProviders.Create(http, OpenRouter, "sk-or-123");

        var request = new ChatRequest("openai/gpt-4o", [ChatMessage.System("Be brief."), ChatMessage.User("Hi", [new ChatImage("image/png", [1, 2, 3])])]);
        var events = await Collect(provider, request);

        Assert.Equal("Hello, world", string.Concat(events.OfType<TextDelta>().Select(e => e.Text)));
        Assert.Equal("Thinking it over.", Assert.Single(events.OfType<ReasoningDelta>()).Text);
        var completed = Assert.IsType<ChatCompleted>(events[^1]);
        Assert.Equal("stop", completed.Reason);
        Assert.Equal(new TokenUsage(12, 3), completed.Usage);
        Assert.Empty(events.OfType<ToolCallsRequested>());

        var (sent, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", sent.RequestUri!.ToString());
        Assert.Equal("Bearer sk-or-123", sent.Headers.Authorization!.ToString());
        Assert.Equal("Coder", sent.Headers.GetValues("X-Title").Single());
        Assert.True(sent.Headers.Contains("HTTP-Referer"));
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("openai/gpt-4o", json.RootElement.GetProperty("model").GetString());
        var messages = json.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        var parts = messages[1].GetProperty("content");
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AQID", parts[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.False(json.RootElement.TryGetProperty("tools", out _)); // none offered, none sent
    }

    [Fact]
    public async Task OpenAi_stream_assembles_tool_calls_sent_in_pieces_and_sends_tool_results_back()
    {
        using var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"read_file","arguments":""}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a.txt\"}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"list_directory","arguments":"{\"path\":\".\"}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            "[DONE]"));
        using var http = new HttpClient(handler);
        var provider = AiProviders.Create(http, FreeLlm, "freellmapi-key");
        var call = new ToolCall("call_0", "find_files", """{"pattern":"*.cs"}""");
        var request = new ChatRequest("auto", [ChatMessage.User("Look"), ChatMessage.Assistant(string.Empty, [call]), ChatMessage.ToolResult(call, "a.cs")])
        {
            Tools = AgentToolbox.Definitions,
        };

        var events = await Collect(provider, request);

        var calls = Assert.Single(events.OfType<ToolCallsRequested>()).Calls;
        Assert.Equal(2, calls.Count);
        Assert.Equal(new ToolCall("call_a", "read_file", """{"path":"a.txt"}"""), calls[0]);
        Assert.Equal("list_directory", calls[1].Name);
        Assert.Equal("tool_calls", Assert.IsType<ChatCompleted>(events[^1]).Reason);

        var (sent, body) = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:3001/v1/chat/completions", sent.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body);
        var messages = json.RootElement.GetProperty("messages");
        Assert.Equal(JsonValueKind.Null, messages[1].GetProperty("content").ValueKind);
        Assert.Equal("find_files", messages[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call_0", messages[2].GetProperty("tool_call_id").GetString());
        var tools = json.RootElement.GetProperty("tools");
        Assert.Equal(AgentToolbox.Definitions.Count, tools.GetArrayLength());
        Assert.Equal("object", tools[0].GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task OpenAi_errors_carry_the_services_message()
    {
        using var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("""{"error":{"message":"Invalid API key provided","code":401}}""", HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);
        var provider = AiProviders.Create(http, OpenRouter, "wrong");

        var error = await Assert.ThrowsAsync<AiProviderException>(() => Collect(provider, new ChatRequest("m", [ChatMessage.User("Hi")])));

        Assert.Equal(401, error.StatusCode);
        Assert.Contains("Invalid API key provided", error.Message);
        Assert.Contains("refused", error.Message);
        Assert.StartsWith("OpenRouter:", error.Message);
    }

    [Fact]
    public async Task OpenAi_error_mid_stream_and_tools_unsupported_are_recognized()
    {
        using var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Sse(
            """{"choices":[{"delta":{"content":"Par"}}]}""",
            """{"error":{"message":"Upstream overloaded"}}"""));
        using var http = new HttpClient(handler);
        var provider = AiProviders.Create(http, OpenRouter, "k");
        var error = await Assert.ThrowsAsync<AiProviderException>(() => Collect(provider, new ChatRequest("m", [ChatMessage.User("Hi")])));
        Assert.Contains("Upstream overloaded", error.Message);
        Assert.False(error.ToolsUnsupported);

        Assert.True(new AiProviderException("No endpoints found that support tool use.").ToolsUnsupported);
        Assert.True(new AiProviderException("Ollama: registry.ollama.ai/library/gemma:2b does not support tools").ToolsUnsupported);
    }

    [Fact]
    public async Task OpenAi_server_that_ignores_streaming_still_works()
    {
        using var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(
            """{"choices":[{"message":{"role":"assistant","content":"Whole answer","tool_calls":[{"id":"c1","type":"function","function":{"name":"read_file","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":5,"completion_tokens":2}}"""));
        using var http = new HttpClient(handler);
        var events = await Collect(AiProviders.Create(http, OpenRouter, "k"), new ChatRequest("m", [ChatMessage.User("Hi")]));

        Assert.Equal("Whole answer", Assert.Single(events.OfType<TextDelta>()).Text);
        Assert.Equal("read_file", Assert.Single(Assert.Single(events.OfType<ToolCallsRequested>()).Calls).Name);
        Assert.Equal(new TokenUsage(5, 2), Assert.IsType<ChatCompleted>(events[^1]).Usage);
    }

    [Fact]
    public async Task Unreachable_server_says_how_to_fix_it()
    {
        using var handler = new FakeHttpHandler((_, _) => throw new HttpRequestException("No connection could be made"));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<AiProviderException>(() => AiProviders.Create(http, FreeLlm, "k").ListModelsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Could not reach FreeLLMAPI at http://localhost:3001/v1", error.Message);
        Assert.Contains("Is FreeLLMAPI running?", error.Message);
    }

    [Fact]
    public async Task Models_lists_read_capabilities_and_freellmapi_readiness()
    {
        using var handler = new FakeHttpHandler((request, _) => request.RequestUri!.Host == "openrouter.ai"
            ? FakeHttpHandler.Json("""
                {"data":[
                  {"id":"openai/gpt-4o","name":"OpenAI: GPT-4o","context_length":128000,"architecture":{"input_modalities":["text","image"]},"supported_parameters":["tools","temperature"]},
                  {"id":"meta/tiny","name":"Tiny","architecture":{"input_modalities":["text"]},"supported_parameters":["temperature"]}
                ]}
                """)
            : FakeHttpHandler.Json("""
                {"object":"list","data":[
                  {"id":"gemini-2.5-flash","execution_status":"ready"},
                  {"id":"auto","execution_status":"ready"},
                  {"id":"gpt-oss-120b","execution_status":"needsKey"}
                ]}
                """));
        using var http = new HttpClient(handler);

        var openRouter = await AiProviders.Create(http, OpenRouter, "k").ListModelsAsync(TestContext.Current.CancellationToken);
        var gpt = openRouter.Single(m => m.Id == "openai/gpt-4o");
        Assert.Equal("OpenAI: GPT-4o", gpt.DisplayName);
        Assert.True(gpt.SupportsVision);
        Assert.True(gpt.SupportsTools);
        Assert.Equal(128000, gpt.ContextLength);
        var tiny = openRouter.Single(m => m.Id == "meta/tiny");
        Assert.False(tiny.SupportsVision);
        Assert.False(tiny.SupportsTools);

        var free = await AiProviders.Create(http, FreeLlm, "k").ListModelsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["auto", "gemini-2.5-flash"], free.Select(m => m.Id)); // the router first; models needing a key left out
        Assert.Equal("http://localhost:3001/v1/models", handler.Requests[^1].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Ollama_streams_ndjson_with_thinking_and_tool_calls()
    {
        using var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Ndjson(
            """{"model":"qwen3","message":{"role":"assistant","content":"","thinking":"Hmm."},"done":false}""",
            """{"model":"qwen3","message":{"role":"assistant","content":"Let me look."},"done":false}""",
            """{"model":"qwen3","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"read_file","arguments":{"path":"a.txt"}}}]},"done":false}""",
            """{"model":"qwen3","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":30,"eval_count":9}"""));
        using var http = new HttpClient(handler);
        var provider = AiProviders.Create(http, Ollama, null);
        var call = new ToolCall("c0", "list_directory", """{"path":"."}""");
        var request = new ChatRequest("qwen3", [ChatMessage.User("Hi", [new ChatImage("image/jpeg", [9])]), ChatMessage.Assistant(string.Empty, [call]), ChatMessage.ToolResult(call, "a.txt")])
        {
            Tools = AgentToolbox.Definitions,
            Temperature = 0.2,
        };

        var events = await Collect(provider, request);

        Assert.Equal("Hmm.", Assert.Single(events.OfType<ReasoningDelta>()).Text);
        Assert.Equal("Let me look.", Assert.Single(events.OfType<TextDelta>()).Text);
        var requested = Assert.Single(Assert.Single(events.OfType<ToolCallsRequested>()).Calls);
        Assert.Equal("read_file", requested.Name);
        Assert.Equal("""{"path":"a.txt"}""", requested.Arguments);
        Assert.Equal(new TokenUsage(30, 9), Assert.IsType<ChatCompleted>(events[^1]).Usage);

        var (sent, body) = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:11434/api/chat", sent.RequestUri!.ToString());
        Assert.Null(sent.Headers.Authorization);
        using var json = JsonDocument.Parse(body);
        var messages = json.RootElement.GetProperty("messages");
        Assert.Equal("CQ==", messages[0].GetProperty("images")[0].GetString());
        Assert.Equal(".", messages[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetProperty("path").GetString());
        Assert.Equal("list_directory", messages[2].GetProperty("tool_name").GetString());
        Assert.Equal(0.2, json.RootElement.GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.Equal(AgentToolbox.Definitions.Count, json.RootElement.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task Ollama_lists_pulled_models_with_their_capabilities()
    {
        using var handler = new FakeHttpHandler((request, body) => request.RequestUri!.AbsolutePath switch
        {
            "/api/tags" => FakeHttpHandler.Json("""{"models":[{"name":"llava:7b"},{"name":"qwen3:8b"}]}"""),
            "/api/show" when body.Contains("llava", StringComparison.Ordinal) => FakeHttpHandler.Json("""{"capabilities":["completion","vision"],"model_info":{"llama.context_length":4096}}"""),
            "/api/show" => FakeHttpHandler.Json("""{"capabilities":["completion","tools","thinking"]}"""),
            _ => FakeHttpHandler.Json("{}", HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler);

        // A base URL typed with OpenAI's "/v1" still reaches Ollama's own API.
        var models = await AiProviders.Create(http, Ollama with { BaseUrl = "localhost:11434/v1" }, null).ListModelsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["llava:7b", "qwen3:8b"], models.Select(m => m.Id));
        Assert.True(models[0].SupportsVision);
        Assert.False(models[0].SupportsTools);
        Assert.Equal(4096, models[0].ContextLength);
        Assert.True(models[1].SupportsTools);
        Assert.All(handler.Requests, r => Assert.StartsWith("http://localhost:11434/api/", r.Request.RequestUri!.ToString()));
    }

    [Fact]
    public async Task Ollama_error_lines_become_exceptions()
    {
        using var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("""{"error":"model \"nope\" not found, try pulling it first"}""", HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<AiProviderException>(() => Collect(AiProviders.Create(http, Ollama, null), new ChatRequest("nope", [ChatMessage.User("Hi")])));

        Assert.Contains("try pulling it first", error.Message);
    }

    [Fact]
    public void Base_urls_are_normalized()
    {
        Assert.Equal("http://localhost:3001/v1", AiProviders.NormalizeUrl(" localhost:3001/v1/ "));
        Assert.Equal("https://api.example.com/v1", AiProviders.NormalizeUrl("api.example.com/v1"));
        Assert.Equal("http://192.168.1.5:11434", AiProviders.NormalizeUrl("http://192.168.1.5:11434/"));
    }

    [Fact]
    public void Api_keys_are_encrypted_on_disk_and_survive_a_restart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // DPAPI is Windows only; elsewhere keys last for the session
        }

        var folder = Directory.CreateTempSubdirectory("coder-keys-").FullName;
        try
        {
            var path = Path.Combine(folder, "agent", "api-keys.bin");
            var store = new DpapiApiKeyStore(path);
            store.Set("openrouter", " sk-or-secret-value ");
            store.Set("ollama", null);

            Assert.True(File.Exists(path));
            Assert.DoesNotContain("sk-or-secret-value", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            Assert.DoesNotContain("sk-or-secret-value", Encoding.Unicode.GetString(File.ReadAllBytes(path)));
            Assert.Equal("sk-or-secret-value", new DpapiApiKeyStore(path).Get("openrouter"));

            store.Set("openrouter", string.Empty);
            Assert.Null(new DpapiApiKeyStore(path).Get("openrouter"));

            File.WriteAllBytes(path, [1, 2, 3]); // damaged: no key, no crash
            Assert.Null(new DpapiApiKeyStore(path).Get("openrouter"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
