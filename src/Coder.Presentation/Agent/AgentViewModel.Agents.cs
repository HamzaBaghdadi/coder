using System.Collections.Specialized;
using System.Text;
using Coder.Core.Acp;
using Coder.Core.Ai;
using Coder.Presentation.Editors;
using Coder.Presentation.Palette;

namespace Coder.Presentation.Agent;

/// <summary>
/// Coder Agent with a coding agent from an extension (<see cref="AgentServer"/>), over the Agent Client
/// Protocol: the agent runs its own loop and tools; Coder shows its replies, reasoning and tool calls, and
/// asks the user when it asks permission. One session per chat; New Chat starts another.
/// </summary>
public sealed partial class AgentViewModel
{
    /// <summary>Starts <see cref="ProviderId"/> when an agent, not a model, is picked.</summary>
    public const string AgentProviderPrefix = "agent:";

    /// <summary>How long an agent may take to start and answer <c>initialize</c>.</summary>
    public static readonly TimeSpan AgentStartTimeout = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, AgentConnection> _agentConnections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolCallItem> _agentTools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AcpToolContent>> _agentToolContent = new(StringComparer.Ordinal);
    private string? _agentSessionId;
    private string? _agentSessionFor;
    private NoticeItem? _agentPlan;
    private bool _agentsWatched;

    /// <summary>The coding agents extensions bring.</summary>
    public IReadOnlyList<AgentServer> AgentServers => _workspace.AgentServers;

    /// <summary>The agent picked instead of a model, or null.</summary>
    public AgentServer? SelectedAgent => _providerId.StartsWith(AgentProviderPrefix, StringComparison.Ordinal)
        ? _workspace.AgentServers.FirstOrDefault(a => a.ProviderId == _providerId)
        : null;

    /// <summary>The picker's first rows: the agents, for the chat (not for inline suggestions).</summary>
    private List<PaletteItem> AgentRows() =>
    [
        .. _workspace.AgentServers.Select(agent => new PaletteItem(agent.Name, $"Agent · {agent.Source}", agent.ProviderId == _providerId ? "current" : null, () =>
        {
            SelectModel(agent.ProviderId, agent.Id);
            return Task.CompletedTask;
        })),
    ];

    /// <summary>Ends every running agent's process (Coder is closing).</summary>
    public async Task StopAgentsAsync()
    {
        Stop();
        foreach (var connection in _agentConnections.Values.ToList())
        {
            if (connection.Client is { } client)
            {
                await client.DisposeAsync();
            }
        }

        _agentConnections.Clear();
        ResetAgentSession();
    }

    /// <summary>Ends the agent's session (New Chat): the next message opens a new one.</summary>
    private void ResetAgentSession()
    {
        _agentSessionId = null;
        _agentSessionFor = null;
        _agentTools.Clear();
        _agentToolContent.Clear();
        _agentPlan = null;
    }

    /// <summary>Agents uninstalled while running are stopped.</summary>
    private void WatchAgentServers()
    {
        if (_agentsWatched)
        {
            return;
        }

        _agentsWatched = true;
        _workspace.AgentServers.CollectionChanged += (_, e) =>
        {
            foreach (var removed in e.OldItems?.OfType<AgentServer>() ?? [])
            {
                if (e.Action != NotifyCollectionChangedAction.Add && _agentConnections.Remove(removed.Id, out var connection))
                {
                    _ = connection.Client?.DisposeAsync().AsTask();
                    if (_agentSessionFor == removed.Id)
                    {
                        ResetAgentSession();
                    }
                }
            }

            OnPropertyChanged(nameof(ModelLabel));
        };
    }

    /// <summary>Sends the message to the picked agent and shows its turn.</summary>
    private async Task SendToAgentAsync(AgentServer agent, string text, IReadOnlyList<AgentAttachment> attachments)
    {
        Items.Add(new UserMessageItem(text, attachments));
        OnPropertyChanged(nameof(HasMessages));
        using var run = new CancellationTokenSource();
        _run = run;
        IsBusy = true;
        try
        {
            var client = await ConnectAgentAsync(agent, run.Token);
            if (_agentSessionId is null || _agentSessionFor != agent.Id)
            {
                _agentSessionId = await client.NewSessionAsync(WorkingDirectory(), run.Token);
                _agentSessionFor = agent.Id;
            }

            var prompt = await BuildAgentPromptAsync(text, attachments, client.AcceptsImages, agent.Name);
            var stop = await client.PromptAsync(_agentSessionId, prompt, run.Token);
            EndStreaming();
            switch (stop.Value)
            {
                case "cancelled":
                    AddNotice("Stopped.", isError: false);
                    break;
                case "max_tokens":
                    AddNotice($"{agent.Name} reached its output limit and stopped.", isError: false);
                    break;
                case "max_turn_requests":
                    AddNotice($"{agent.Name} stopped after too many steps. Send \"continue\" to let it go on.", isError: false);
                    break;
                case "refusal":
                    AddNotice($"{agent.Name} declined to continue.", isError: false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            AddNotice("Stopped.", isError: false);
        }
        catch (AcpException exception)
        {
            AddNotice(exception.AuthRequired
                ? $"{agent.Name} needs you to sign in first. {exception.Message}"
                : $"{agent.Name}: {exception.Message}", isError: true);
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            AddNotice($"{agent.Name} couldn't start: {exception.Message}", isError: true);
        }
        finally
        {
            EndStreaming();
            CancelPending();
            _run = null;
            IsBusy = false;
        }
    }

    /// <summary>The agent's running connection, starting its program (after the user's consent) when needed.</summary>
    private async Task<AcpClient> ConnectAgentAsync(AgentServer agent, CancellationToken cancellationToken)
    {
        WatchAgentServers();
        if (_agentConnections.TryGetValue(agent.Id, out var existing) && existing.Client is { IsClosed: false } running)
        {
            return running;
        }

        if (!await agent.AllowAsync())
        {
            throw new AcpException($"{agent.Source} isn't allowed to run on this computer. Allow it to use {agent.Name}.");
        }

        var connection = new AgentConnection(this);
        var start = _services.StartAgent ?? ((server, folder, host) => AcpClient.Start(server.Program, server.Arguments, folder, null, host));
        var client = start(agent, WorkingDirectory(), connection);
        connection.Client = client;
        _agentConnections[agent.Id] = connection;
        client.Exited += (_, _) => Post(() =>
        {
            if (_agentConnections.TryGetValue(agent.Id, out var current) && ReferenceEquals(current, connection))
            {
                _agentConnections.Remove(agent.Id);
                if (_agentSessionFor == agent.Id)
                {
                    ResetAgentSession(); // its sessions ended with it
                }
            }
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AgentStartTimeout);
        try
        {
            await client.InitializeAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await client.DisposeAsync();
            throw new AcpException($"{agent.Name} didn't answer within {AgentStartTimeout.TotalSeconds:N0} seconds.");
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }

        return client;
    }

    private string WorkingDirectory() => _workspace.Root() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The message as ACP prompt blocks: its text with attached files (as for models), and images when the agent takes them.</summary>
    private async Task<IReadOnlyList<AcpPromptBlock>> BuildAgentPromptAsync(string text, IReadOnlyList<AgentAttachment> attachments, bool acceptsImages, string agentName)
    {
        var message = await BuildMessageAsync(text, attachments, model: null);
        var blocks = new List<AcpPromptBlock> { new AcpTextBlock(message.Text) };
        if (message.Images.Count > 0 && !acceptsImages)
        {
            AddNotice($"{agentName} doesn't take images, so they were left out.", isError: false);
        }
        else
        {
            blocks.AddRange(message.Images.Select(image => new AcpImageBlock(image.MediaType, image.Data)));
        }

        return blocks;
    }

    // Updates ----------------------------------------------------------------------------------------

    private void HandleAgentUpdate(string sessionId, AcpUpdate update)
    {
        if (sessionId != _agentSessionId)
        {
            return; // an older session's late update
        }

        switch (update)
        {
            case AcpMessageChunk message:
                Streaming().AppendText(message.Text);
                break;
            case AcpThoughtChunk thought:
                Streaming().AppendReasoning(thought.Text);
                break;
            case AcpToolCall call:
                ApplyToolCall(call);
                break;
            case AcpPlan plan:
                ShowPlan(plan);
                break;
        }
    }

    /// <summary>A tool call starts or changes: its card in the chat, with its status and result.</summary>
    private ToolCallItem ApplyToolCall(AcpToolCall call)
    {
        if (!_agentTools.TryGetValue(call.Id, out var tool))
        {
            EndStreaming();
            tool = new ToolCallItem(new ToolCall(call.Id, call.Kind ?? "other", call.RawInput ?? "{}"), call.Title ?? call.Kind ?? "Tool");
            _agentTools[call.Id] = tool;
            Items.Add(tool);
        }

        if (call.Content is { } content)
        {
            _agentToolContent[call.Id] = [.. content];
            var text = string.Join("\n", content.Select(c => c switch
            {
                AcpTextContent t => t.Text,
                AcpDiffContent d => $"Changed {DisplayPath(d.Path)}",
                _ => null,
            }).OfType<string>());
            if (text.Length > 0)
            {
                tool.Result = text;
            }
        }

        // Waiting for the user stays until they decide; the agent's status follows.
        if (call.Status is { } status && tool.Status != ToolStatus.WaitingForUser)
        {
            tool.Status = status switch
            {
                "completed" => ToolStatus.Succeeded,
                "failed" => ToolStatus.Failed,
                _ => ToolStatus.Running,
            };
        }

        return tool;
    }

    /// <summary>The agent's plan as a checklist, replacing the previous one in place.</summary>
    private void ShowPlan(AcpPlan plan)
    {
        var text = new StringBuilder("Plan:");
        foreach (var (content, status) in plan.Entries)
        {
            text.Append('\n').Append(status switch { "completed" => "✓ ", "in_progress" => "▸ ", _ => "○ " }).Append(content);
        }

        var notice = new NoticeItem(text.ToString(), isError: false);
        var index = _agentPlan is null ? -1 : Items.IndexOf(_agentPlan);
        if (index >= 0)
        {
            Items[index] = notice;
        }
        else
        {
            EndStreaming();
            Items.Add(notice);
        }

        _agentPlan = notice;
    }

    /// <summary>The agent asks permission: a card on its tool call with the agent's choices; waits for one.</summary>
    private async Task<string?> AskPermissionAsync(AcpPermissionRequest request, CancellationToken cancellationToken)
    {
        if (request.SessionId != _agentSessionId || _run is not { } run)
        {
            return null; // not this chat's turn
        }

        var tool = ApplyToolCall(request.ToolCall);
        var change = (request.ToolCall.Content ?? (_agentToolContent.TryGetValue(request.ToolCall.Id, out var known) ? known : null))?
            .OfType<AcpDiffContent>().FirstOrDefault();
        var proposal = change is null ? null : new EditProposal(change.Path, change.OldText, change.NewText);
        var card = new PermissionProposalItem(request.Options, proposal, change is null ? null : DisplayPath(change.Path), OpenPermissionDiff, _workspace.OpenFile);
        tool.Proposal = card;
        tool.Status = ToolStatus.WaitingForUser;

        // Stopping the turn answers "cancelled" at once, as ACP asks (the agent waits for the answer to stop).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Token);
        await using (linked.Token.Register(() => Post(card.Cancel)))
        {
            var chosen = await card.Decision;
            tool.Status = chosen is null ? ToolStatus.Failed : card.Chosen?.Allows == true ? ToolStatus.Running : ToolStatus.Failed;
            if (card.Chosen is { Allows: false } rejected)
            {
                tool.Result = $"Not allowed ({rejected.Name}).";
            }

            return chosen;
        }
    }

    private void OpenPermissionDiff(PermissionProposalItem item)
    {
        if (item.Change is { } change)
        {
            _workspace.Editors.OpenDiff(new DiffTabViewModel(change.Path, item.IsNewFile ? "New File" : "Current", change.Original ?? string.Empty, SelectedAgent?.Name ?? "Agent", change.Proposed));
        }
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    /// <summary>One agent's connection: its updates and permission requests go to the chat, on the UI thread.</summary>
    private sealed class AgentConnection(AgentViewModel chat) : IAcpClientHost
    {
        public AcpClient? Client { get; set; }

        public void OnUpdate(string sessionId, AcpUpdate update) => chat.Post(() => chat.HandleAgentUpdate(sessionId, update));

        public Task<string?> RequestPermissionAsync(AcpPermissionRequest request, CancellationToken cancellationToken) =>
            chat.OnUi(() => chat.AskPermissionAsync(request, cancellationToken));
    }
}
