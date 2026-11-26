namespace Coder.Core.Ai;

/// <summary>The kinds of chat service Coder Agent talks to.</summary>
public enum AiProviderKind
{
    /// <summary>Any server with OpenAI's <c>/chat/completions</c> API (OpenAI, LM Studio, vLLM, Groq, DeepSeek…).</summary>
    OpenAiCompatible,

    /// <summary>openrouter.ai: one key for hundreds of models. OpenAI-compatible, with model capabilities in its list.</summary>
    OpenRouter,

    /// <summary>FreeLLMAPI (github.com/tashfeenahmed/freellmapi): a self-hosted OpenAI-compatible router over free providers.</summary>
    FreeLlmApi,

    /// <summary>Ollama: models running on this machine (or the network), no key.</summary>
    Ollama,
}

/// <summary>A configured provider: what kind it is, what the user calls it and where it is. Its key is kept apart (<see cref="IApiKeyStore"/>).</summary>
public sealed record AiProviderConfig(string Id, AiProviderKind Kind, string Name, string BaseUrl);

/// <summary>Defaults and construction for each kind of provider.</summary>
public static class AiProviders
{
    public const string OpenRouterUrl = "https://openrouter.ai/api/v1";

    /// <summary>FreeLLMAPI's router when run locally (its dashboard is on the same port).</summary>
    public const string FreeLlmApiUrl = "http://localhost:3001/v1";

    public const string OllamaUrl = "http://localhost:11434";

    public const string OpenAiUrl = "https://api.openai.com/v1";

    /// <summary>The providers a new installation starts with: each needs only a key (or, for Ollama, nothing).</summary>
    public static IReadOnlyList<AiProviderConfig> Defaults { get; } =
    [
        new("freellmapi", AiProviderKind.FreeLlmApi, "FreeLLMAPI", FreeLlmApiUrl),
        new("openrouter", AiProviderKind.OpenRouter, "OpenRouter", OpenRouterUrl),
        new("ollama", AiProviderKind.Ollama, "Ollama", OllamaUrl),
    ];

    public static string DefaultUrl(AiProviderKind kind) => kind switch
    {
        AiProviderKind.OpenRouter => OpenRouterUrl,
        AiProviderKind.FreeLlmApi => FreeLlmApiUrl,
        AiProviderKind.Ollama => OllamaUrl,
        _ => OpenAiUrl,
    };

    public static string KindName(AiProviderKind kind) => kind switch
    {
        AiProviderKind.OpenRouter => "OpenRouter",
        AiProviderKind.FreeLlmApi => "FreeLLMAPI",
        AiProviderKind.Ollama => "Ollama",
        _ => "OpenAI-compatible",
    };

    /// <summary>Whether requests need a key: Ollama runs without one (a key is still sent when given, for proxies).</summary>
    public static bool NeedsKey(AiProviderKind kind) => kind != AiProviderKind.Ollama;

    /// <summary>Where the user gets a key, shown next to the key box.</summary>
    public static string KeyHint(AiProviderKind kind) => kind switch
    {
        AiProviderKind.OpenRouter => "Create a key at openrouter.ai/keys.",
        AiProviderKind.FreeLlmApi => "The unified key (freellmapi-…) from the Keys page of your FreeLLMAPI dashboard (localhost:3001).",
        AiProviderKind.Ollama => "No key needed. Start Ollama and pull a model (ollama pull qwen3).",
        _ => "The API key your provider gave you.",
    };

    /// <summary>The model to use when the user has not picked one and the list is unavailable.</summary>
    public static string? DefaultModel(AiProviderKind kind) => kind == AiProviderKind.FreeLlmApi ? "auto" : null;

    /// <summary>Creates the client for <paramref name="config"/>. <paramref name="http"/> must not time out by itself (replies stream for minutes).</summary>
    public static IChatProvider Create(HttpClient http, AiProviderConfig config, string? apiKey) =>
        config.Kind == AiProviderKind.Ollama
            ? new OllamaProvider(http, config, apiKey)
            : new OpenAiCompatibleProvider(http, config, apiKey);

    /// <summary>A base URL as typed, made usable: trimmed, with a scheme, without a trailing slash.</summary>
    public static string NormalizeUrl(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (trimmed.Length > 0 && !trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = (trimmed.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("127.", StringComparison.Ordinal) ? "http://" : "https://") + trimmed;
        }

        return trimmed;
    }
}
