using System.Text.Json;
using System.Text.Json.Nodes;
using Coder.Core.Extensions;
using Coder.Core.Lsp;
using Coder.Presentation.Commands;
using Coder.Presentation.Services;

namespace Coder.Presentation.Extensions;

/// <summary>Starts an extension's program; tests supply an in-memory one.</summary>
public interface IExtensionLauncher
{
    /// <exception cref="System.ComponentModel.Win32Exception">It couldn't be started.</exception>
    ILanguageServerTransport Start(InstalledExtension extension, IReadOnlyDictionary<string, string?> environment);
}

/// <summary>Runs the program as a child process in the extension's folder.</summary>
public sealed class ProcessExtensionLauncher : IExtensionLauncher
{
    public ILanguageServerTransport Start(InstalledExtension extension, IReadOnlyDictionary<string, string?> environment) =>
        ProcessTransport.Start(extension.ProgramPath!, [], extension.Folder, environment);
}

/// <summary>What Coder offers extension programs; the host routes their requests here (on the UI thread).</summary>
public interface IEditorApi
{
    /// <summary>The request methods a program may call ("editor/open"...).</summary>
    IReadOnlyCollection<string> Methods { get; }

    /// <summary>Sent with <c>initialize</c>: Coder's version, process id, open folders.</summary>
    JsonObject InitializeParameters();

    /// <summary>Answers one request; throws (with a message for the program) when it can't.</summary>
    Task<JsonNode?> HandleAsync(InstalledExtension extension, string method, JsonElement parameters, CancellationToken cancellationToken);

    /// <summary>Something every program hears about (the selection moved, a folder opened): method and params.</summary>
    event EventHandler<(string Method, JsonNode? Parameters)>? Notification;

    /// <summary>An extension's program stopped (or crashed): drop what it left on screen (its decorations).</summary>
    void Forget(InstalledExtension extension)
    {
    }
}

/// <summary>
/// Runs the programs of installed extensions (<c>coder.program</c> in package.json): each one as a child
/// process speaking JSON-RPC over stdio (Content-Length framed, like a language server), with the
/// <see cref="IEditorApi"/> to call. A program runs only after the user allowed it on this machine
/// (<see cref="ExtensionTrust"/>); its manifest commands are in the palette from install on, and running
/// one starts it if needed. A program that crashes is restarted up to three times a minute; one being
/// updated or uninstalled is stopped first.
/// </summary>
public sealed class ExtensionHost : IAsyncDisposable
{
    private const int MaxRestarts = 3;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(1);

    private readonly ExtensionStore _store;
    private readonly ExtensionTrust _trust;
    private readonly IDialogService _dialogs;
    private readonly CommandRegistry _commands;
    private readonly IEditorApi _api;
    private readonly IExtensionLauncher _launcher;
    private readonly Action<string> _showMessage;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly Dictionary<string, Running> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _registeredCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<bool>> _asking = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTime>> _crashes = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;
    private bool _disposed;

    private readonly IList<Workbench.ExtensionSideBarView> _views;
    private readonly IList<Agent.AgentServer> _agents;

    /// <param name="views">Where extensions' side-bar views go (the workbench's secondary side bar tabs).</param>
    /// <param name="agents">Where extensions' coding agents go (Coder Agent's picker).</param>
    public ExtensionHost(ExtensionStore store, ExtensionTrust trust, IDialogService dialogs, CommandRegistry commands, IEditorApi api,
        IExtensionLauncher? launcher = null, Action<string>? showMessage = null, IList<Workbench.ExtensionSideBarView>? views = null,
        IList<Agent.AgentServer>? agents = null)
    {
        _views = views ?? [];
        _agents = agents ?? [];
        _store = store;
        _trust = trust;
        _dialogs = dialogs;
        _commands = commands;
        _api = api;
        _launcher = launcher ?? new ProcessExtensionLauncher();
        _showMessage = showMessage ?? (_ => { });
        store.BeforeRemoveAsync = StopAsync;
        store.Changed += (_, _) => Post(() => _ = RefreshAsync());
        api.Notification += (_, n) => Broadcast(n.Method, n.Parameters);
        foreach (var extension in store.Installed())
        {
            RegisterCommands(extension); // in the palette at once; the programs wait for StartAsync
        }
    }

    /// <summary>Raised on the UI thread when a program starts or stops (for views and tests).</summary>
    public event EventHandler? RunningChanged;

    /// <summary>Ids of the extensions whose program is running.</summary>
    public IReadOnlyCollection<string> RunningIds => _running.Keys.ToList();

    /// <summary>Registers the installed extensions' commands and starts their programs (asking first when needed). Call once the window is up.</summary>
    public Task StartAsync()
    {
        _started = true;
        return RefreshAsync();
    }

    /// <summary>Brings commands and running programs in line with what is installed.</summary>
    private async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        var installed = _store.Installed().ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var id in _registeredCommands.Keys.Where(id => !installed.ContainsKey(id)).ToList())
        {
            UnregisterCommands(id);
            RemoveViews(id);
            _trust.Forget(id); // uninstalled: a reinstall asks again
        }

        foreach (var extension in installed.Values)
        {
            RemoveViews(extension.Id, extension.Views.Select(v => v.Id).ToList()); // views an update dropped
        }

        foreach (var id in _running.Keys.Where(id => !installed.TryGetValue(id, out var now) || now.Folder != _running[id].Extension.Folder).ToList())
        {
            await StopAsync(id); // uninstalled, or updated to a new folder
        }

        foreach (var id in _agents.Select(a => a.ExtensionId).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (!installed.TryGetValue(id, out var now) || now.Agents.Count == 0 || _agents.Any(a => a.ExtensionId == id && now.Agents.All(n => n.Id != a.Id || n.Program != a.Program)))
            {
                RemoveAgents(id); // uninstalled, or its agents changed (RegisterCommands adds the new ones)
            }
        }

        foreach (var extension in installed.Values)
        {
            UnregisterCommands(extension.Id);
            RegisterCommands(extension);
        }

        if (!_started)
        {
            return;
        }

        foreach (var extension in installed.Values.Where(e => e.ProgramPath is not null && !_running.ContainsKey(e.Id)))
        {
            if (await IsAllowedAsync(extension, askAgain: false))
            {
                await StartProgramAsync(extension);
            }
        }
    }

    // Commands ----------------------------------------------------------------------------------

    private void RegisterCommands(InstalledExtension extension)
    {
        foreach (var view in extension.Views.Where(v => _views.All(existing => existing.Id != v.Id)))
        {
            _views.Add(new Workbench.ExtensionSideBarView(view.Id, view.Title, extension.Id,
                () => view.Command is { } command ? RunCommandAsync(extension.Id, command) : Task.CompletedTask));
        }

        // Its agents, for this platform; starting one asks the same consent as its program.
        foreach (var agent in extension.Agents.Where(a => a.Program is not null && _agents.All(existing => existing.Id != a.Id)))
        {
            _agents.Add(new Agent.AgentServer(agent.Id, agent.Name, agent.Program!, agent.Arguments, extension.DisplayName)
            {
                ExtensionId = extension.Id,
                AllowAsync = () => IsAllowedAsync(extension, askAgain: true),
            });
        }

        var ids = new List<string>();
        foreach (var command in extension.Commands.Where(c => !_commands.Contains(c.Id)))
        {
            _commands.Register(new CommandDefinition(command.Id, command.Category, command.Title, command.KeyBinding, () => RunCommandAsync(extension.Id, command.Id)));
            ids.Add(command.Id);
        }

        _registeredCommands[extension.Id] = ids;
    }

    /// <summary>Removes an extension's side-bar views, except those its (updated) manifest still declares, which keep their terminal.</summary>
    private void RemoveViews(string id, IReadOnlyCollection<string>? keep = null)
    {
        foreach (var view in _views.Where(v => string.Equals(v.ExtensionId, id, StringComparison.OrdinalIgnoreCase) && keep?.Contains(v.Id) != true).ToList())
        {
            _views.Remove(view);
        }
    }

    /// <summary>Removes an extension's agents (uninstalled, or updated: the update registers its agents again).</summary>
    private void RemoveAgents(string id)
    {
        foreach (var agent in _agents.Where(a => string.Equals(a.ExtensionId, id, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _agents.Remove(agent);
        }
    }

    private void UnregisterCommands(string id)
    {
        if (_registeredCommands.Remove(id, out var ids))
        {
            ids.ForEach(_commands.Unregister);
        }
    }

    /// <summary>Runs a manifest command: starts the program first when it isn't running (asking, if it was refused before).</summary>
    public async Task RunCommandAsync(string extensionId, string commandId)
    {
        if (_store.Find(extensionId) is not { } extension)
        {
            return;
        }

        if (extension.ProgramPath is null)
        {
            _showMessage(extension.DeclaresProgram ? $"{extension.DisplayName} isn't available for this computer ({Platform.RuntimeId})." : $"{extension.DisplayName} has no program to run {commandId}.");
            return;
        }

        if (!_running.ContainsKey(extensionId) && (!await IsAllowedAsync(extension, askAgain: true) || !await StartProgramAsync(extension)))
        {
            return;
        }

        try
        {
            await _running[extensionId].Connection.RequestAsync("commands/execute", new JsonObject { ["command"] = commandId });
        }
        catch (Exception exception) when (exception is IOException or LanguageServerException or ObjectDisposedException or KeyNotFoundException)
        {
            _showMessage($"{extension.DisplayName}: {exception.Message}");
        }
    }

    // Consent -----------------------------------------------------------------------------------

    /// <summary>Whether the user allows this package's program here; asks once when they haven't said (or, for a command, when they refused before).</summary>
    private Task<bool> IsAllowedAsync(InstalledExtension extension, bool askAgain)
    {
        switch (_trust.For(extension))
        {
            case ExtensionTrust.Decision.Allowed:
                return Task.FromResult(true);
            case ExtensionTrust.Decision.Denied when !askAgain:
                return Task.FromResult(false);
        }

        if (_asking.TryGetValue(extension.Id, out var question))
        {
            return question; // already on screen: one question, however many callers
        }

        question = AskAsync(extension);
        if (!question.IsCompleted)
        {
            _asking[extension.Id] = question; // AskAsync removes it once answered
        }

        return question;
    }

    private async Task<bool> AskAsync(InstalledExtension extension)
    {
        try
        {
            var allowed = await _dialogs.ConfirmAsync(
                $"Allow {extension.DisplayName} to run on this computer?",
                $"{extension.DisplayName} (by {extension.Publisher}) includes a program. Coder starts it and lets it read and edit your open files, show changes for you to accept, open terminals and run commands. Only allow extensions you trust.",
                "Allow");
            _trust.Set(extension, allowed);
            return allowed;
        }
        finally
        {
            _asking.Remove(extension.Id);
        }
    }

    // Processes ---------------------------------------------------------------------------------

    private async Task<bool> StartProgramAsync(InstalledExtension extension)
    {
        if (_running.ContainsKey(extension.Id))
        {
            return true;
        }

        ILanguageServerTransport transport;
        try
        {
            transport = _launcher.Start(extension, new Dictionary<string, string?>
            {
                ["CODER_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CODER_EXTENSION_ID"] = extension.Id,
                ["CODER_EXTENSION_FOLDER"] = extension.Folder,
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _showMessage($"{extension.DisplayName} couldn't start: {exception.Message}");
            return false;
        }

        var connection = new JsonRpcConnection(transport.Input, transport.Output);
        var running = new Running(extension, transport, connection);
        foreach (var method in _api.Methods)
        {
            connection.OnRequestAsync(method, (parameters, token) => OnUi(() => _api.HandleAsync(extension, method, parameters, token)));
        }

        connection.OnNotification("window/log", _ => { }); // programs may log; nothing to show yet
        connection.Closed += (_, _) => Post(() => OnClosed(running));
        _running[extension.Id] = running;
        connection.Start();

        try
        {
            using var timeout = new CancellationTokenSource(StartupTimeout);
            var parameters = _api.InitializeParameters();
            parameters["extension"] = new JsonObject { ["id"] = extension.Id, ["version"] = extension.Version, ["folder"] = extension.Folder };
            await connection.RequestAsync("initialize", parameters, timeout.Token);
            connection.Notify("initialized", new JsonObject());
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or LanguageServerException or ObjectDisposedException)
        {
            _running.Remove(extension.Id);
            running.Stopping = true;
            connection.Dispose();
            transport.Dispose();
            _showMessage($"{extension.DisplayName} didn't start: {Explain(exception, transport)}");
            RunningChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        RunningChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The connection ended: on purpose (stopping), or a crash, which restarts it a few times.</summary>
    private void OnClosed(Running running)
    {
        if (running.Stopping || !_running.TryGetValue(running.Extension.Id, out var current) || current != running)
        {
            return;
        }

        _running.Remove(running.Extension.Id);
        running.Transport.Dispose();
        _api.Forget(running.Extension); // a restart sets its decorations again
        RunningChanged?.Invoke(this, EventArgs.Empty);
        if (_disposed)
        {
            return;
        }

        var crashes = _crashes.TryGetValue(running.Extension.Id, out var list) ? list : _crashes[running.Extension.Id] = [];
        crashes.RemoveAll(t => DateTime.UtcNow - t > RestartWindow);
        crashes.Add(DateTime.UtcNow);
        if (crashes.Count > MaxRestarts)
        {
            _showMessage($"{running.Extension.DisplayName} stopped unexpectedly and won't be restarted. {Last(running.Transport.RecentErrorOutput)}".TrimEnd());
            return;
        }

        _ = StartProgramAsync(running.Extension);
    }

    /// <summary>Stops an extension's program, politely first (<c>shutdown</c>, then <c>exit</c>), then for good.</summary>
    public async Task StopAsync(string id)
    {
        if (!_running.Remove(id, out var running))
        {
            return;
        }

        running.Stopping = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await running.Connection.RequestAsync("shutdown", null, timeout.Token);
            running.Connection.Notify("exit", null);
            await Task.Delay(50);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or LanguageServerException or ObjectDisposedException)
        {
            // it didn't answer: it is ended below
        }

        running.Connection.Dispose();
        running.Transport.Dispose();
        _api.Forget(running.Extension);
        RunningChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var id in _running.Keys.ToList())
        {
            await StopAsync(id);
        }
    }

    private void Broadcast(string method, JsonNode? parameters)
    {
        foreach (var running in _running.Values)
        {
            running.Connection.Notify(method, parameters?.DeepClone());
        }
    }

    // Threads -----------------------------------------------------------------------------------

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

    /// <summary>Runs an API call on the UI thread (requests arrive on the connection's thread).</summary>
    private Task<JsonNode?> OnUi(Func<Task<JsonNode?>> action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            return action();
        }

        var done = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(async _ =>
        {
            try
            {
                done.TrySetResult(await action());
            }
            catch (OperationCanceledException)
            {
                done.TrySetCanceled();
            }
            catch (Exception exception)
            {
                done.TrySetException(exception);
            }
        }, null);
        return done.Task;
    }

    private static string Explain(Exception exception, ILanguageServerTransport transport) =>
        Last(transport.RecentErrorOutput) is { Length: > 0 } error ? error
        : exception is OperationCanceledException ? "it didn't answer in time."
        : exception.Message;

    private static string Last(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? string.Empty;

    private sealed class Running(InstalledExtension extension, ILanguageServerTransport transport, JsonRpcConnection connection)
    {
        public InstalledExtension Extension { get; } = extension;

        public ILanguageServerTransport Transport { get; } = transport;

        public JsonRpcConnection Connection { get; } = connection;

        public bool Stopping { get; set; }
    }
}
