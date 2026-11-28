using System.Collections.ObjectModel;
using System.Windows.Input;
using Coder.Core.Ai;
using Coder.Core.Files;
using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Palette;
using Coder.Presentation.Settings;

namespace Coder.Presentation.Agent;

/// <summary>What Coder Agent needs from outside: the network, the key store and a shell.</summary>
/// <param name="Http">For the providers; must not time out by itself (replies stream for minutes).</param>
public sealed record AgentServices(HttpClient Http, IApiKeyStore Keys, ICommandRunner Commands)
{
    /// <summary>Creates provider clients; tests can swap it.</summary>
    public Func<AiProviderConfig, string?, IChatProvider>? CreateProvider { get; init; }

    /// <summary>Starts a coding agent's program and connects to it (agent, working folder, host); tests can swap it.</summary>
    public Func<AgentServer, string, Coder.Core.Acp.IAcpClientHost, Coder.Core.Acp.AcpClient>? StartAgent { get; init; }
}

/// <summary>What Coder Agent needs from the workbench.</summary>
public sealed record AgentWorkspace(
    Func<string?> Root,
    IFileSystem FileSystem,
    EditorGroupViewModel Editors,
    Action<IReadOnlyList<(string Path, Coder.Core.Lsp.FileChangeType Type)>> FilesChanged,
    Func<string, Task> OpenFile,
    CommandPaletteViewModel Palette)
{
    /// <summary>The coding agents extensions bring (kept up to date by the extension host).</summary>
    public ObservableCollection<AgentServer> AgentServers { get; init; } = [];
}

/// <summary>
/// Coder Agent, the chat in the secondary side bar: messages and the agent's work (tools, diffs to accept,
/// commands to approve), the input with its attachments, and the provider and model it talks to.
/// </summary>
public sealed partial class AgentViewModel : ObservableObject
{
    /// <summary>A text file longer than this is cut when attached.</summary>
    public const int MaxAttachmentChars = 200_000;

    /// <summary>Images bigger than this are refused (providers reject them anyway).</summary>
    public const int MaxImageBytes = 10 * 1024 * 1024;

    private readonly AgentServices _services;
    private readonly AgentWorkspace _workspace;
    private readonly SynchronizationContext? _ui;
    private readonly List<ChatMessage> _conversation = [];
    private readonly Dictionary<string, bool> _toolSupport = new(StringComparer.Ordinal);
    private CancellationTokenSource? _run;
    private string _input = string.Empty;
    private bool _isBusy;
    private bool _isProvidersOpen;
    private string _providerId;
    private string _model;
    private string? _notice;
    private string _usage = string.Empty;
    private AssistantMessageItem? _streaming;
    private ToolCallItem? _currentTool;
    private int _pasteCount;

    public AgentViewModel(AgentServices services, AgentWorkspace workspace, UserSettings settings)
    {
        _services = services;
        _workspace = workspace;
        _ui = SynchronizationContext.Current;
        var configured = settings.AgentProviders is { } list
            ? list.Where(p => p is { Id.Length: > 0 }).Select(p => new AiProviderConfig(p.Id, AgentProviderViewModel.ParseKind(p.Kind), p.Name ?? string.Empty, p.BaseUrl ?? string.Empty))
            : AiProviders.Defaults;
        foreach (var config in configured)
        {
            Providers.Add(CreateProvider(config));
        }

        _providerId = settings.AgentProvider ?? string.Empty;
        _model = settings.AgentModel ?? string.Empty;
        SendCommand = new RelayCommand(() => _ = SendAsync());
        StopCommand = new RelayCommand(Stop);
        NewChatCommand = new RelayCommand(NewChat);
        PickModelCommand = new RelayCommand(() => _ = ShowModelPickerAsync());
        ToggleProvidersCommand = new RelayCommand(() => IsProvidersOpen = !IsProvidersOpen);
        RemoveAttachmentCommand = new RelayCommand(item =>
        {
            if (item is AgentAttachment attachment)
            {
                Attachments.Remove(attachment);
            }
        });
        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanSend));
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMessages));
        WatchAgentServers();
    }

    public ObservableCollection<ChatItem> Items { get; } = [];

    public ObservableCollection<AgentAttachment> Attachments { get; } = [];

    public ObservableCollection<AgentProviderViewModel> Providers { get; } = [];

    /// <summary>Raised when settings to save changed (providers, the chosen model).</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Asks the view to put the caret in the input.</summary>
    public event EventHandler? FocusRequested;

    public string Input
    {
        get => _input;
        set
        {
            if (SetProperty(ref _input, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanSend));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSend));
            }
        }
    }

    public bool CanSend => !IsBusy && (Input.Trim().Length > 0 || Attachments.Count > 0);

    public bool HasMessages => Items.Count > 0;

    /// <summary>The providers page (keys, addresses) shows instead of the chat.</summary>
    public bool IsProvidersOpen
    {
        get => _isProvidersOpen;
        set => SetProperty(ref _isProvidersOpen, value);
    }

    /// <summary>A short message above the input (an attachment refused, no model chosen…); null when none.</summary>
    public string? Notice
    {
        get => _notice;
        set => SetProperty(ref _notice, value);
    }

    /// <summary>Tokens used by the last request, when the provider said.</summary>
    public string Usage
    {
        get => _usage;
        private set => SetProperty(ref _usage, value);
    }

    public AgentProviderViewModel? Provider => Providers.FirstOrDefault(p => p.Id == _providerId);

    public string Model => _model.Length > 0 ? _model : Provider is { } provider ? AiProviders.DefaultModel(provider.Kind) ?? string.Empty : string.Empty;

    public string ProviderId => _providerId;

    /// <summary>The model button's text.</summary>
    public string ModelLabel => SelectedAgent is { } agent ? $"{agent.Name} · Agent"
        : _providerId.StartsWith(AgentProviderPrefix, StringComparison.Ordinal) ? "Pick a model"
        : Provider is { } provider && Model.Length > 0
        ? $"{(provider.FindModel(Model)?.DisplayName ?? Model)} · {provider.Config.Name}"
        : "Pick a model";

    public ICommand SendCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand NewChatCommand { get; }

    public ICommand PickModelCommand { get; }

    public ICommand ToggleProvidersCommand { get; }

    public ICommand RemoveAttachmentCommand { get; }

    public IReadOnlyList<AgentProviderSetting> ProviderSettings => Providers.Select(p => p.ToSetting()).ToList();

    /// <summary>Switches to a model (remembered in settings).</summary>
    public void SelectModel(string providerId, string model)
    {
        _providerId = providerId;
        _model = model;
        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(ModelLabel));
        OnPropertyChanged(nameof(ProviderId));
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Focus() => FocusRequested?.Invoke(this, EventArgs.Empty);

    public void NewChat()
    {
        Stop();
        Items.Clear();
        _conversation.Clear();
        ResetAgentSession();
        Attachments.Clear();
        Notice = null;
        Usage = string.Empty;
        OnPropertyChanged(nameof(HasMessages));
        Focus();
    }

    public void Stop() => _run?.Cancel();

    /// <summary>Sends the input and the attachments, then runs the agent until it answers.</summary>
    public async Task SendAsync()
    {
        if (!CanSend)
        {
            return;
        }

        if (_providerId.StartsWith(AgentProviderPrefix, StringComparison.Ordinal))
        {
            if (SelectedAgent is not { } agent)
            {
                Notice = "That agent isn't installed anymore. Pick another.";
                await ShowModelPickerAsync();
                return;
            }

            var prompt = Input.Trim();
            var attached = Attachments.ToList();
            Input = string.Empty;
            Attachments.Clear();
            Notice = null;
            await SendToAgentAsync(agent, prompt, attached);
            return;
        }

        if (Provider is not { } provider || Model.Length == 0)
        {
            Notice = "Pick a model first.";
            await ShowModelPickerAsync();
            return;
        }

        if (!provider.IsReady)
        {
            Notice = $"{provider.Config.Name} needs an API key. Add it in the providers page.";
            IsProvidersOpen = true;
            return;
        }

        var text = Input.Trim();
        var attachments = Attachments.ToList();
        Input = string.Empty;
        Attachments.Clear();
        Notice = null;
        var message = await BuildMessageAsync(text, attachments, provider.FindModel(Model));
        Items.Add(new UserMessageItem(text, attachments));
        OnPropertyChanged(nameof(HasMessages));
        _conversation.Add(message);
        await RunAsync(provider, Model);
    }

    /// <summary>Runs one agent turn on the conversation as it stands (a new message, or Retry after an error).</summary>
    private async Task RunAsync(AgentProviderViewModel provider, string model)
    {
        var root = _workspace.Root();
        var toolbox = root is null ? null : new AgentToolbox(root, _workspace.FileSystem, new ToolHost(this), _services.Commands);
        var info = provider.FindModel(model);
        var key = $"{provider.Id}\n{model}";
        var loop = new AgentLoop(provider.CreateClient(), toolbox)
        {
            UseTools = toolbox is not null && (_toolSupport.TryGetValue(key, out var known) ? known : info?.SupportsTools != false),
        };

        // The system prompt says what the agent can do now (a folder may have been opened since).
        var system = ChatMessage.System(AgentLoop.SystemPrompt(root, loop.UseTools));
        if (_conversation.Count > 0 && _conversation[0].Role == ChatRole.System)
        {
            _conversation[0] = system;
        }
        else
        {
            _conversation.Insert(0, system);
        }

        using var run = new CancellationTokenSource();
        _run = run;
        IsBusy = true;
        try
        {
            await foreach (var item in loop.RunAsync(_conversation, model, run.Token))
            {
                Handle(item);
            }
        }
        catch (OperationCanceledException)
        {
            AddNotice("Stopped.", isError: false);
        }
        catch (AiProviderException exception)
        {
            AddNotice(exception.Message, isError: true, retry: new RelayCommand(() => _ = RetryAsync()));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException)
        {
            AddNotice($"{provider.Config.Name}: {exception.Message}", isError: true, retry: new RelayCommand(() => _ = RetryAsync()));
        }
        finally
        {
            if (!loop.UseTools && toolbox is not null)
            {
                _toolSupport[key] = false;
            }

            EndStreaming();
            CancelPending();
            _currentTool = null;
            _run = null;
            IsBusy = false;
        }
    }

    private async Task RetryAsync()
    {
        if (IsBusy || Provider is not { } provider || Model.Length == 0)
        {
            return;
        }

        if (Items.Count > 0 && Items[^1] is NoticeItem { IsError: true } failed)
        {
            Items.Remove(failed);
        }

        await RunAsync(provider, Model);
    }

    private void Handle(AgentEvent item)
    {
        switch (item)
        {
            case AgentTextDelta delta:
                Streaming().AppendText(delta.Text);
                break;
            case AgentReasoningDelta reasoning:
                Streaming().AppendReasoning(reasoning.Text);
                break;
            case AgentToolStarted started:
                EndStreaming();
                _currentTool = new ToolCallItem(started.Call, started.Title);
                Items.Add(_currentTool);
                break;
            case AgentToolFinished finished:
                if (_currentTool is { } tool && tool.Call == finished.Call)
                {
                    tool.Result = finished.Result.Text;
                    tool.Status = finished.Result.IsError ? ToolStatus.Failed : ToolStatus.Succeeded;
                }

                _currentTool = null;
                break;
            case AgentNotice notice:
                EndStreaming();
                AddNotice(notice.Text, isError: false);
                break;
            case AgentStepCompleted step:
                EndStreaming();
                if (step.Usage is { } usage)
                {
                    Usage = $"{usage.PromptTokens:N0} in · {usage.CompletionTokens:N0} out";
                }

                break;
        }
    }

    private AssistantMessageItem Streaming()
    {
        if (_streaming is null)
        {
            _streaming = new AssistantMessageItem();
            Items.Add(_streaming);
        }

        return _streaming;
    }

    private void EndStreaming()
    {
        if (_streaming is { } message)
        {
            message.Complete();
            if (message.Text.Trim().Length == 0 && !message.HasReasoning)
            {
                Items.Remove(message);
            }

            _streaming = null;
        }
    }

    private void CancelPending()
    {
        foreach (var tool in Items.OfType<ToolCallItem>())
        {
            switch (tool.Proposal)
            {
                case EditProposalItem edit:
                    edit.Cancel();
                    break;
                case CommandProposalItem command:
                    command.Cancel();
                    break;
                case PermissionProposalItem permission:
                    permission.Cancel();
                    break;
            }

            if (tool.Status is ToolStatus.Running or ToolStatus.WaitingForUser)
            {
                tool.Status = ToolStatus.Failed;
                tool.Result = tool.Result.Length > 0 ? tool.Result : "Stopped.";
            }
        }
    }

    private void AddNotice(string text, bool isError, ICommand? retry = null) => Items.Add(new NoticeItem(text, isError, retry));

    // Models ---------------------------------------------------------------------------------------

    /// <summary>Lists every provider's models in the palette (after fetching them), plus a way to type any model id.</summary>
    public Task ShowModelPickerAsync() => ShowModelPickerAsync(
        _workspace.AgentServers.Count > 0 ? "Pick an agent or a model for Coder Agent (or type a model id)" : "Pick a model for Coder Agent (or type a model id)",
        _providerId, Model, SelectModel, AgentRows());

    /// <summary>The model picker for another use (inline suggestions): its title, the current choice, what picking does and rows to add first.</summary>
    public async Task ShowModelPickerAsync(string title, string currentProvider, string currentModel, Action<string, string> select, IReadOnlyList<PaletteItem>? extraRows = null)
    {
        Notice = "Loading models…";
        await Task.WhenAll(Providers.Where(p => p.IsReady).Select(p => p.GetModelsAsync()));
        Notice = null;
        var rows = new List<PaletteItem>();
        foreach (var provider in Providers)
        {
            var name = provider.Config.Name;
            if (!provider.IsReady)
            {
                rows.Add(new PaletteItem($"{name}: add an API key…", provider.KindName, null, () => OpenProviders()));
                continue;
            }

            if (provider.Error is { } error)
            {
                rows.Add(new PaletteItem($"{name}: unavailable", error, null, () => OpenProviders()));
                continue;
            }

            foreach (var model in provider.Models ?? [])
            {
                var traits = string.Join(" · ", new[]
                {
                    name,
                    model.SupportsTools == true ? "tools" : model.SupportsTools == false ? "no tools" : null,
                    model.SupportsVision == true ? "vision" : null,
                    model.ContextLength is { } context ? $"{context / 1000:N0}K context" : null,
                }.Where(t => t is not null));
                var current = provider.Id == currentProvider && model.Id == currentModel;
                rows.Add(new PaletteItem(model.DisplayName, model.Id == model.DisplayName ? traits : $"{model.Id} · {traits}", current ? "current" : null, () =>
                {
                    select(provider.Id, model.Id);
                    return Task.CompletedTask;
                }));
            }
        }

        rows.InsertRange(0, extraRows ?? []);
        rows.Add(new PaletteItem("Manage providers and API keys…", "Coder Agent", null, () => OpenProviders()));
        var ready = Providers.Where(p => p.IsReady).ToList();
        _workspace.Palette.Pick(new PickProvider(title, query =>
        {
            var ranked = CommandPaletteViewModel.Rank(rows, query).ToList();
            if (query.Trim().Length > 0 && !rows.Any(r => r.Label == query.Trim()))
            {
                // Any model id the provider knows, even one it does not list ("auto:smart", a new release).
                ranked.AddRange(ready.Select(provider => new PaletteItem($"Use \"{query.Trim()}\"", $"with {provider.Config.Name}", null, () =>
                {
                    select(provider.Id, query.Trim());
                    return Task.CompletedTask;
                })));
            }

            return ranked;
        }));
    }

    private Task OpenProviders()
    {
        IsProvidersOpen = true;
        return Task.CompletedTask;
    }

    // Providers ------------------------------------------------------------------------------------

    /// <summary>Adds a provider of this kind with its default address; it still needs a key (except Ollama).</summary>
    public AgentProviderViewModel AddProvider(AiProviderKind kind)
    {
        var name = AiProviders.KindName(kind);
        var id = AgentProviderViewModel.KindId(kind);
        for (var n = 2; Providers.Any(p => p.Id == id); n++)
        {
            id = $"{AgentProviderViewModel.KindId(kind)}-{n}";
            name = $"{AiProviders.KindName(kind)} {n}";
        }

        var provider = CreateProvider(new AiProviderConfig(id, kind, name, AiProviders.DefaultUrl(kind)));
        Providers.Add(provider);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        return provider;
    }

    public ICommand AddProviderCommand => new RelayCommand(kind =>
    {
        if (kind is AiProviderKind value)
        {
            AddProvider(value);
        }
    });

    private AgentProviderViewModel CreateProvider(AiProviderConfig config)
    {
        var create = _services.CreateProvider ?? ((c, key) => AiProviders.Create(_services.Http, c, key));
        var provider = new AgentProviderViewModel(config, _services.Keys, create, RemoveProvider);
        provider.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(ModelLabel));
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        };
        provider.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AgentProviderViewModel.Models) && provider.Id == _providerId)
            {
                OnPropertyChanged(nameof(ModelLabel));
            }
        };
        return provider;
    }

    private void RemoveProvider(AgentProviderViewModel provider)
    {
        provider.SetKey(null);
        Providers.Remove(provider);
        if (provider.Id == _providerId)
        {
            SelectModel(string.Empty, string.Empty);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
