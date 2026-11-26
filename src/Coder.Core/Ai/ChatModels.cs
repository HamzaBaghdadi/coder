namespace Coder.Core.Ai;

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>An image sent to a vision model: its bytes and media type ("image/png").</summary>
public sealed record ChatImage(string MediaType, byte[] Data)
{
    public string ToDataUrl() => $"data:{MediaType};base64,{Convert.ToBase64String(Data)}";
}

/// <summary>A tool the model asked to run: the provider's id for the call, the tool's name and its JSON arguments.</summary>
public sealed record ToolCall(string Id, string Name, string Arguments);

/// <summary>
/// One message of a conversation. User messages may carry images; assistant messages may ask for
/// tools (<see cref="ToolCalls"/>); tool messages answer one call (<see cref="ToolCallId"/>).
/// </summary>
public sealed record ChatMessage(ChatRole Role, string Text)
{
    public IReadOnlyList<ChatImage> Images { get; init; } = [];

    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>For a tool message: the call it answers.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>For a tool message: the tool that ran (Ollama wants the name, not an id).</summary>
    public string? ToolName { get; init; }

    public static ChatMessage System(string text) => new(ChatRole.System, text);

    public static ChatMessage User(string text, IReadOnlyList<ChatImage>? images = null) => new(ChatRole.User, text) { Images = images ?? [] };

    public static ChatMessage Assistant(string text, IReadOnlyList<ToolCall>? toolCalls = null) => new(ChatRole.Assistant, text) { ToolCalls = toolCalls ?? [] };

    public static ChatMessage ToolResult(ToolCall call, string result) => new(ChatRole.Tool, result) { ToolCallId = call.Id, ToolName = call.Name };
}

/// <summary>A tool the model may call: a name, what it does, and a JSON schema of its arguments.</summary>
public sealed record ToolDefinition(string Name, string Description, string ParametersSchema);

/// <summary>What to send: the model, the conversation so far and the tools it may use (none for plain chat).</summary>
public sealed record ChatRequest(string Model, IReadOnlyList<ChatMessage> Messages)
{
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];

    public double? Temperature { get; init; }
}

/// <summary>Something that arrived while a reply streams in.</summary>
public abstract record ChatStreamEvent;

/// <summary>More of the reply's text.</summary>
public sealed record TextDelta(string Text) : ChatStreamEvent;

/// <summary>More of the model's visible reasoning (models that think out loud: DeepSeek R1, Qwen 3…).</summary>
public sealed record ReasoningDelta(string Text) : ChatStreamEvent;

/// <summary>The reply asks for these tools, all of them complete.</summary>
public sealed record ToolCallsRequested(IReadOnlyList<ToolCall> Calls) : ChatStreamEvent;

/// <summary>The reply is complete. <paramref name="Reason"/> is the provider's ("stop", "length", "tool_calls"…).</summary>
public sealed record ChatCompleted(string? Reason, TokenUsage? Usage) : ChatStreamEvent;

public sealed record TokenUsage(int PromptTokens, int CompletionTokens);

/// <summary>A model a provider offers. Null capabilities are unknown (most OpenAI-compatible servers don't say).</summary>
public sealed record ModelInfo(string Id, string? Name = null)
{
    public bool? SupportsVision { get; init; }

    public bool? SupportsTools { get; init; }

    public int? ContextLength { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

/// <summary>A chat service: OpenAI-compatible servers (OpenRouter, FreeLLMAPI, OpenAI…) or Ollama.</summary>
public interface IChatProvider
{
    AiProviderConfig Config { get; }

    /// <summary>The models the user can pick.</summary>
    /// <exception cref="AiProviderException">The service could not be reached or refused.</exception>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken);

    /// <summary>Streams one reply. Cancelling stops it (the request is aborted).</summary>
    /// <exception cref="AiProviderException">The service could not be reached, refused, or failed mid-reply.</exception>
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest chat, CancellationToken cancellationToken);
}

/// <summary>Why a provider failed, in words for the user (the service's own message when it sent one).</summary>
public sealed class AiProviderException : Exception
{
    public AiProviderException()
    {
    }

    public AiProviderException(string message)
        : base(message)
    {
    }

    public AiProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AiProviderException(string message, int? statusCode, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status, when the service answered with an error.</summary>
    public int? StatusCode { get; }

    /// <summary>The model (or the route to it) can't call tools; asking again without tools may work.</summary>
    public bool ToolsUnsupported =>
        Message.Contains("support tool", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("tools are not supported", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("tool use", StringComparison.OrdinalIgnoreCase) && Message.Contains("support", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("function calling", StringComparison.OrdinalIgnoreCase) && Message.Contains("support", StringComparison.OrdinalIgnoreCase);
}
