namespace Coder.Presentation.Agent;

/// <summary>
/// A coding agent Coder Agent can talk to over the Agent Client Protocol: a program Coder starts, with its
/// arguments. Extensions bring them (<c>coder.agents</c>); picking one in the model picker sends the chat to it.
/// </summary>
/// <param name="Id">Unique among agents ("claude-code.agent").</param>
/// <param name="Name">Shown in the picker and on the model button ("Claude Code").</param>
/// <param name="Source">Who brings it ("Claude Code for Coder"), shown beside the name.</param>
public sealed record AgentServer(string Id, string Name, string Program, IReadOnlyList<string> Arguments, string Source)
{
    /// <summary>The extension it comes from, if any (removed with it).</summary>
    public string? ExtensionId { get; init; }

    /// <summary>Asks the user's consent before the program starts (the extension's); false when refused.</summary>
    public Func<Task<bool>> AllowAsync { get; init; } = () => Task.FromResult(true);

    /// <summary>The value of <see cref="AgentViewModel.ProviderId"/> while this agent is picked.</summary>
    public string ProviderId => AgentViewModel.AgentProviderPrefix + Id;
}
