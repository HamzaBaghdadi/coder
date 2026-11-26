using System.Runtime.CompilerServices;

namespace Coder.Core.Ai;

/// <summary>Something that happened during an agent turn, for the chat to show.</summary>
public abstract record AgentEvent;

public sealed record AgentTextDelta(string Text) : AgentEvent;

public sealed record AgentReasoningDelta(string Text) : AgentEvent;

/// <summary>A tool is about to run (or to wait for the user's approval).</summary>
public sealed record AgentToolStarted(ToolCall Call, string Title) : AgentEvent;

public sealed record AgentToolFinished(ToolCall Call, ToolResult Result) : AgentEvent;

/// <summary>Something the user should know that is not the model's words ("this model can't use tools").</summary>
public sealed record AgentNotice(string Text) : AgentEvent;

/// <summary>One request to the model finished (an agent turn may make several: one after each round of tools).</summary>
public sealed record AgentStepCompleted(TokenUsage? Usage) : AgentEvent;

/// <summary>
/// Runs an agent turn: sends the conversation, streams the reply, runs the tools it asks for, sends the
/// results back, and repeats until the model answers without tools. The conversation list grows with
/// every assistant and tool message, so the next turn continues where this one ended.
/// </summary>
public sealed class AgentLoop(IChatProvider provider, AgentToolbox? tools)
{
    /// <summary>Rounds of tool calls in one turn before the agent stops and asks the user to continue.</summary>
    public int MaxSteps { get; init; } = 40;

    public double? Temperature { get; init; }

    /// <summary>Whether to offer tools; set to false when the model turned out not to support them.</summary>
    public bool UseTools { get; set; } = true;

    public async IAsyncEnumerable<AgentEvent> RunAsync(List<ChatMessage> conversation, string model, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var step = 0; ; step++)
        {
            if (step >= MaxSteps)
            {
                yield return new AgentNotice($"Stopped after {MaxSteps} rounds of tool use. Send \"continue\" to let the agent go on.");
                yield break;
            }

            var offerTools = UseTools && tools is not null;
            var request = new ChatRequest(model, conversation) { Tools = offerTools ? AgentToolbox.Definitions : [], Temperature = Temperature };
            var text = new System.Text.StringBuilder();
            IReadOnlyList<ToolCall> calls = [];
            TokenUsage? usage = null;
            var retryWithoutTools = false;

            await using (var stream = provider.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    ChatStreamEvent item;
                    try
                    {
                        if (!await stream.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        item = stream.Current;
                    }
                    catch (AiProviderException exception) when (offerTools && text.Length == 0 && exception.ToolsUnsupported)
                    {
                        retryWithoutTools = true;
                        break;
                    }

                    switch (item)
                    {
                        case TextDelta delta:
                            text.Append(delta.Text);
                            yield return new AgentTextDelta(delta.Text);
                            break;
                        case ReasoningDelta reasoning:
                            yield return new AgentReasoningDelta(reasoning.Text);
                            break;
                        case ToolCallsRequested requested:
                            calls = requested.Calls;
                            break;
                        case ChatCompleted completed:
                            usage = completed.Usage;
                            break;
                    }
                }
            }

            if (retryWithoutTools)
            {
                UseTools = false;
                yield return new AgentNotice($"{model} can't use tools, so the agent can't read or change files with it; it answers from the chat alone. Pick a model with tool support for agent work.");
                step--;
                continue;
            }

            conversation.Add(ChatMessage.Assistant(text.ToString(), calls));
            yield return new AgentStepCompleted(usage);
            if (calls.Count == 0 || tools is null)
            {
                yield break;
            }

            foreach (var call in calls)
            {
                yield return new AgentToolStarted(call, AgentToolbox.Describe(call));
                ToolResult result;
                try
                {
                    result = await tools.InvokeAsync(call, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Keep the conversation valid for the next turn: every call gets an answer.
                    conversation.Add(ChatMessage.ToolResult(call, "Error: the user stopped the agent before this tool finished."));
                    foreach (var rest in calls.SkipWhile(c => c != call).Skip(1))
                    {
                        conversation.Add(ChatMessage.ToolResult(rest, "Error: not run; the user stopped the agent."));
                    }

                    throw;
                }

                conversation.Add(ChatMessage.ToolResult(call, result.Text));
                yield return new AgentToolFinished(call, result);
            }
        }
    }

    /// <summary>The system prompt: who the agent is, where it works and how to use its tools.</summary>
    public static string SystemPrompt(string? workspaceRoot, bool hasTools)
    {
        var os = OperatingSystem.IsWindows() ? "Windows (commands run in PowerShell)" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
        var prompt = new System.Text.StringBuilder();
        prompt.Append("You are Coder Agent, an expert software engineer working inside the Coder code editor. ");
        prompt.Append("Answer clearly and concisely in Markdown; put code in fenced blocks with a language.\n");
        prompt.Append($"Operating system: {os}. Today is {DateTime.Now:yyyy-MM-dd}.\n");
        if (workspaceRoot is null)
        {
            prompt.Append("No folder is open in the editor, so there are no workspace files to work with; files the user attaches are included in their messages.\n");
        }
        else if (hasTools)
        {
            prompt.Append($"The open workspace folder is {workspaceRoot}. Use the tools to explore and change it: ");
            prompt.Append("find and read files before changing them, make focused edits with edit_file (write_file for new files), and keep the existing style. ");
            prompt.Append("Every edit is shown to the user as a diff to accept or reject, and every command needs their approval, so explain briefly why before each one. ");
            prompt.Append("Paths are relative to the workspace folder. When you are done, summarize what you changed.\n");
        }
        else
        {
            prompt.Append($"The open workspace folder is {workspaceRoot}, but this model has no tool access: you can't read or change files yourself. Work from what the user attaches or pastes, and give changes as code blocks.\n");
        }

        prompt.Append("Attached files appear in the user's message inside <file path=\"…\"> tags.");
        return prompt.ToString();
    }
}
