using System.Text.Json;

namespace Coder.Core.Acp;

/// <summary>
/// Types of the Agent Client Protocol (agentclientprotocol.com), the open protocol between editors and
/// coding agents: what an agent tells the editor during a turn, and what it asks of it. Only what Coder
/// uses is modelled; the rest of a message is ignored.
/// </summary>
public static class AcpProtocol
{
    /// <summary>The protocol version Coder speaks.</summary>
    public const int Version = 1;
}

/// <summary>Who the agent says it is, from <c>initialize</c>.</summary>
public sealed record AcpAgentInfo(string Name, string? Title, string? Version)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Name : Title!;
}

/// <summary>A way to sign in to the agent (<c>authMethods</c> from <c>initialize</c>).</summary>
public sealed record AcpAuthMethod(string Id, string Name, string? Description);

/// <summary>Something an agent reports during a turn (<c>session/update</c>).</summary>
public abstract record AcpUpdate;

/// <summary>Part of the agent's reply (<c>agent_message_chunk</c>).</summary>
public sealed record AcpMessageChunk(string Text) : AcpUpdate;

/// <summary>Part of the agent's reasoning (<c>agent_thought_chunk</c>).</summary>
public sealed record AcpThoughtChunk(string Text) : AcpUpdate;

/// <summary>
/// A tool call started (<c>tool_call</c>), or changed (<c>tool_call_update</c>, <see cref="IsUpdate"/>): an update
/// carries only what changed, so null means "unchanged" there.
/// </summary>
public sealed record AcpToolCall(string Id) : AcpUpdate
{
    public bool IsUpdate { get; init; }

    public string? Title { get; init; }

    /// <summary><c>read</c>, <c>edit</c>, <c>delete</c>, <c>move</c>, <c>search</c>, <c>execute</c>, <c>think</c>, <c>fetch</c>, <c>switch_mode</c> or <c>other</c>.</summary>
    public string? Kind { get; init; }

    /// <summary><c>pending</c>, <c>in_progress</c>, <c>completed</c> or <c>failed</c>.</summary>
    public string? Status { get; init; }

    /// <summary>What the tool produced: text and diffs (null in an update: unchanged; when sent, it replaces the old content).</summary>
    public IReadOnlyList<AcpToolContent>? Content { get; init; }

    /// <summary>The files it touches, as absolute paths.</summary>
    public IReadOnlyList<string>? Locations { get; init; }

    /// <summary>The tool's input, as the agent sent it (any JSON), or null.</summary>
    public string? RawInput { get; init; }
}

/// <summary>Part of a tool call's content.</summary>
public abstract record AcpToolContent;

public sealed record AcpTextContent(string Text) : AcpToolContent;

/// <summary>A change to a file: its old text (null for a new file) and new text.</summary>
public sealed record AcpDiffContent(string Path, string? OldText, string NewText) : AcpToolContent;

/// <summary>The agent's plan for the turn (<c>plan</c>): each entry's text and status (<c>pending</c>, <c>in_progress</c>, <c>completed</c>).</summary>
public sealed record AcpPlan(IReadOnlyList<(string Content, string Status)> Entries) : AcpUpdate;

/// <summary>One choice the agent offers when it asks permission.</summary>
/// <param name="Kind"><c>allow_once</c>, <c>allow_always</c>, <c>reject_once</c> or <c>reject_always</c>.</param>
public sealed record AcpPermissionOption(string Id, string Name, string Kind)
{
    public bool Allows => Kind.StartsWith("allow", StringComparison.Ordinal);
}

/// <summary>The agent asks before running a tool (<c>session/request_permission</c>).</summary>
public sealed record AcpPermissionRequest(string SessionId, AcpToolCall ToolCall, IReadOnlyList<AcpPermissionOption> Options);

/// <summary>Why a turn ended: <c>end_turn</c>, <c>max_tokens</c>, <c>max_turn_requests</c>, <c>refusal</c> or <c>cancelled</c>.</summary>
public sealed record AcpStopReason(string Value)
{
    public bool IsCancelled => Value == "cancelled";
}

/// <summary>Part of a prompt: text, or an image (for agents that take images).</summary>
public abstract record AcpPromptBlock;

public sealed record AcpTextBlock(string Text) : AcpPromptBlock;

public sealed record AcpImageBlock(string MediaType, byte[] Data) : AcpPromptBlock;

/// <summary>An agent failed or refused, in words for the user.</summary>
public sealed class AcpException : Exception
{
    public AcpException()
    {
    }

    public AcpException(string message)
        : base(message)
    {
    }

    public AcpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The agent wants the user to sign in first (ACP's <c>auth_required</c> error).</summary>
    public bool AuthRequired { get; init; }
}

/// <summary>Reads ACP's JSON into the types above, leniently: unknown fields and variants are skipped.</summary>
public static class AcpJson
{
    /// <summary>The update in a <c>session/update</c> notification's params, or null for a variant Coder doesn't show.</summary>
    public static (string SessionId, AcpUpdate Update)? ParseUpdate(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || String(parameters, "sessionId") is not { } sessionId
            || !parameters.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        AcpUpdate? parsed = String(update, "sessionUpdate") switch
        {
            "agent_message_chunk" => Text(update) is { } text ? new AcpMessageChunk(text) : null,
            "agent_thought_chunk" => Text(update) is { } thought ? new AcpThoughtChunk(thought) : null,
            "tool_call" => ParseToolCall(update, isUpdate: false),
            "tool_call_update" => ParseToolCall(update, isUpdate: true),
            "plan" => update.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array
                ? new AcpPlan([.. entries.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => (String(e, "content") ?? string.Empty, String(e, "status") ?? "pending"))])
                : null,
            _ => null,
        };
        return parsed is null ? null : (sessionId, parsed);
    }

    /// <summary>A <c>tool_call</c>, a <c>tool_call_update</c>, or the <c>toolCall</c> of a permission request.</summary>
    public static AcpToolCall? ParseToolCall(JsonElement call, bool isUpdate)
    {
        if (call.ValueKind != JsonValueKind.Object || String(call, "toolCallId") is not { } id)
        {
            return null;
        }

        List<AcpToolContent>? content = null;
        if (call.TryGetProperty("content", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            content = [];
            foreach (var item in items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            {
                switch (String(item, "type"))
                {
                    case "content" when item.TryGetProperty("content", out var block) && String(block, "type") == "text" && String(block, "text") is { } text:
                        content.Add(new AcpTextContent(text));
                        break;
                    case "diff" when String(item, "path") is { } path && String(item, "newText") is { } newText:
                        content.Add(new AcpDiffContent(path, String(item, "oldText"), newText));
                        break;
                }
            }
        }

        List<string>? locations = null;
        if (call.TryGetProperty("locations", out var places) && places.ValueKind == JsonValueKind.Array)
        {
            locations = [.. places.EnumerateArray().Select(p => String(p, "path")).OfType<string>()];
        }

        return new AcpToolCall(id)
        {
            IsUpdate = isUpdate,
            Title = String(call, "title"),
            Kind = String(call, "kind") ?? (isUpdate ? null : "other"),
            Status = String(call, "status") ?? (isUpdate ? null : "pending"),
            Content = content,
            Locations = locations,
            RawInput = call.TryGetProperty("rawInput", out var raw) && raw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? raw.GetRawText() : null,
        };
    }

    /// <summary>A <c>session/request_permission</c> request's params.</summary>
    public static AcpPermissionRequest? ParsePermissionRequest(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || String(parameters, "sessionId") is not { } sessionId
            || !parameters.TryGetProperty("toolCall", out var call) || ParseToolCall(call, isUpdate: true) is not { } toolCall
            || !parameters.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parsed = options.EnumerateArray()
            .Where(o => o.ValueKind == JsonValueKind.Object && String(o, "optionId") is not null)
            .Select(o => new AcpPermissionOption(String(o, "optionId")!, String(o, "name") ?? String(o, "optionId")!, String(o, "kind") ?? "allow_once"))
            .ToList();
        return parsed.Count == 0 ? null : new AcpPermissionRequest(sessionId, toolCall, parsed);
    }

    private static string? Text(JsonElement update) =>
        update.TryGetProperty("content", out var content) && String(content, "type") == "text" ? String(content, "text") : null;

    internal static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
