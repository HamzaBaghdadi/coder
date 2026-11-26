using System.Collections.Specialized;
using System.ComponentModel;
using Coder.Core.Lsp;
using Coder.Core.Text;
using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.LanguageFeatures;

/// <summary>
/// Connects open documents to language servers. A server starts the first time a file of its
/// language is opened (one per server and project root), every open file is kept in sync with it,
/// and its diagnostics are placed on the matching tabs. The editor asks this class for
/// completions, hovers and definitions.
/// <para>Everything here runs on the UI thread: server events are posted to the synchronization
/// context that created this object.</para>
/// </summary>
public sealed class LanguageServices : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    private readonly ILanguageServerLauncher? _launcher;
    private readonly EditorGroupViewModel _editors;
    private readonly Func<string?> _workspaceFolder;
    private readonly Func<IReadOnlyList<LanguageServerDefinition>> _customServers;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly List<ServerSession> _sessions = [];
    private readonly Dictionary<EditorTabViewModel, TrackedDocument> _documents = [];
    private readonly Dictionary<string, IReadOnlyList<Diagnostic>> _diagnostics = new(DocumentUri.PathComparer);
    private readonly Dictionary<string, Dictionary<ServerSession, IReadOnlyList<Diagnostic>>> _diagnosticsByServer = new(DocumentUri.PathComparer);
    private readonly HashSet<string> _reportedMissing = [];
    private readonly Dictionary<string, Task> _installs = [];
    private readonly Dictionary<EditorTabViewModel, List<TrackedDocument>> _companions = [];
    private string? _installing;
    private int _errorCount;
    private int _warningCount;
    private Task? _disposal;

    /// <param name="customServers">Servers from settings; they take precedence over the built-in ones.</param>
    public LanguageServices(ILanguageServerLauncher? launcher, EditorGroupViewModel editors, Func<string?> workspaceFolder,
        Func<IReadOnlyList<LanguageServerDefinition>>? customServers = null)
    {
        _customServers = customServers ?? (() => []);
        _launcher = launcher;
        _editors = editors;
        _workspaceFolder = workspaceFolder;

        editors.Tabs.CollectionChanged += OnTabsChanged;
        editors.Saved += (_, tab) => OnSaved(tab);
        editors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveEditor))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        };
        foreach (var tab in editors.Tabs.OfType<EditorTabViewModel>())
        {
            Track(tab);
        }
    }

    /// <summary>Raised after any file's diagnostics changed.</summary>
    public event EventHandler? DiagnosticsChanged;

    /// <summary>Every file's current diagnostics, as last reported.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> AllDiagnostics => _diagnostics;

    /// <summary>A server wants files changed (after a command it ran); the owner applies the edit.</summary>
    public event EventHandler<WorkspaceEdit>? EditRequested;

    /// <summary>Something the user should know: a missing server, a server error.</summary>
    public event EventHandler<string>? Notice;

    /// <summary>Errors across all files the servers reported on.</summary>
    public int ErrorCount
    {
        get => _errorCount;
        private set => SetProperty(ref _errorCount, value);
    }

    public int WarningCount
    {
        get => _warningCount;
        private set => SetProperty(ref _warningCount, value);
    }

    /// <summary>The state of the active document's server, for the status bar ("gopls", "gopls: Loading packages").</summary>
    public string StatusText
    {
        get
        {
            if (_editors.ActiveEditor is not { } tab || !_documents.TryGetValue(tab, out var document))
            {
                return _installing ?? string.Empty;
            }

            var session = document.Session;
            return session.State switch
            {
                SessionState.Starting => $"{session.Name}: starting",
                SessionState.Running when session.Progress is { } progress => $"{session.Name}: {progress}",
                SessionState.Running => session.Name,
                _ => $"{session.Name}: stopped",
            };
        }
    }

    /// <summary>Characters after which the active server offers completions without a word (".", "::"...).</summary>
    public IReadOnlyList<string> CompletionTriggerCharacters(EditorTabViewModel tab) =>
        CompletionDocuments(tab).SelectMany(d => d.Session.Client!.Capabilities.CompletionTriggerCharacters).Distinct().ToList();

    public bool CanComplete(EditorTabViewModel tab) => CompletionDocuments(tab).Any();

    /// <summary>The documents whose servers complete in this tab: its main server's, then companions' (Tailwind).</summary>
    private IEnumerable<TrackedDocument> CompletionDocuments(EditorTabViewModel tab) =>
        (ReadyDocument(tab) is { } main ? [main] : Array.Empty<TrackedDocument>())
        .Concat(ReadyCompanions(tab))
        .Where(d => d.Session.Client!.Capabilities.HasCompletion);

    // Requests ----------------------------------------------------------------------------------

    /// <summary>
    /// Completions from every server that completes here, in one list (the main server's first): in a
    /// Tailwind project, class names come with the HTML or TypeScript suggestions.
    /// </summary>
    public async Task<CompletionList?> CompleteAsync(EditorTabViewModel tab, int offset, string? triggerCharacter, CancellationToken cancellationToken)
    {
        // A trigger character only asks the servers that registered it: a space is one of Tailwind's (for the
        // next class name), and asking HTML then would pop up every tag after every space.
        var documents = CompletionDocuments(tab)
            .Where(d => triggerCharacter is null || d.Session.Client!.Capabilities.CompletionTriggerCharacters.Contains(triggerCharacter))
            .ToList();
        if (documents.Count == 0)
        {
            return null;
        }

        var position = tab.Document.GetPosition(offset);
        var lists = await Task.WhenAll(documents.Select(document =>
        {
            Flush(document);
            return Safely(() => document.Session.Client!.CompletionAsync(document.Path, position, triggerCharacter, cancellationToken));
        }));

        var answered = lists.OfType<CompletionList>().ToList();
        return answered.Count == 0 ? null : new CompletionList(answered.SelectMany(l => l.Items).ToList(), answered.Any(l => l.IsIncomplete));
    }

    public async Task<HoverInfo?> HoverAsync(EditorTabViewModel tab, int offset, CancellationToken cancellationToken)
    {
        if (ReadyDocument(tab) is not { } document || !document.Session.Client!.Capabilities.HasHover)
        {
            return await CompanionHoverAsync(tab, offset, cancellationToken);
        }

        Flush(document);
        var position = tab.Document.GetPosition(offset);
        return await Safely(() => document.Session.Client.HoverAsync(document.Path, position, cancellationToken))
               ?? await CompanionHoverAsync(tab, offset, cancellationToken); // a Tailwind class: its CSS
    }

    private async Task<HoverInfo?> CompanionHoverAsync(EditorTabViewModel tab, int offset, CancellationToken cancellationToken)
    {
        var position = tab.Document.GetPosition(offset);
        foreach (var companion in ReadyCompanions(tab).Where(d => d.Session.Client!.Capabilities.HasHover))
        {
            Flush(companion);
            if (await Safely(() => companion.Session.Client!.HoverAsync(companion.Path, position, cancellationToken)) is { } hover)
            {
                return hover;
            }
        }

        return null;
    }

    /// <summary>Where the symbol at <paramref name="offset"/> is defined, or null when unknown.</summary>
    public async Task<Location?> FindDefinitionAsync(EditorTabViewModel tab, int offset, CancellationToken cancellationToken)
    {
        if (ReadyDocument(tab) is not { } document || !document.Session.Client!.Capabilities.HasDefinition)
        {
            return null;
        }

        Flush(document);
        var position = tab.Document.GetPosition(offset);
        var locations = await Safely(() => document.Session.Client.DefinitionAsync(document.Path, position, cancellationToken));
        return locations is { Count: > 0 } ? locations[0] : null;
    }

    /// <summary>True when the active document's server offers a feature (to explain a missing one).</summary>
    public bool Supports(EditorTabViewModel tab, Func<ServerCapabilities, bool> feature) =>
        ReadyDocument(tab)?.Session.Client?.Capabilities is { } capabilities && feature(capabilities);

    /// <summary>Characters after which signature help opens ("(" and "," for most languages).</summary>
    public IReadOnlyList<string> SignatureTriggerCharacters(EditorTabViewModel tab) =>
        ReadyDocument(tab)?.Session.Client?.Capabilities.SignatureTriggerCharacters ?? [];

    public Task<IReadOnlyList<Location>?> FindReferencesAsync(EditorTabViewModel tab, int offset, CancellationToken cancellationToken) =>
        RequestAsync(tab, c => c.HasReferences, (client, path) => client.ReferencesAsync(path, tab.Document.GetPosition(offset), cancellationToken));

    public Task<WorkspaceEdit?> RenameAsync(EditorTabViewModel tab, int offset, string newName, CancellationToken cancellationToken) =>
        RequestAsync(tab, c => c.HasRename, (client, path) => client.RenameAsync(path, tab.Document.GetPosition(offset), newName, cancellationToken));

    public Task<SignatureHelp?> SignatureHelpAsync(EditorTabViewModel tab, int offset, string? triggerCharacter, CancellationToken cancellationToken) =>
        RequestAsync(tab, c => c.HasSignatureHelp, (client, path) => client.SignatureHelpAsync(path, tab.Document.GetPosition(offset), triggerCharacter, cancellationToken));

    public Task<IReadOnlyList<TextReplacement>?> FormatAsync(EditorTabViewModel tab, CancellationToken cancellationToken) =>
        RequestAsync(tab, c => c.HasFormatting, (client, path) =>
            client.FormattingAsync(path, tab.Editor.Options.TabSize, tab.Editor.Options.InsertSpaces, cancellationToken));

    /// <summary>Quick fixes and refactorings for a range, with the problems reported there.</summary>
    public Task<IReadOnlyList<CodeAction>?> CodeActionsAsync(EditorTabViewModel tab, int start, int end, CancellationToken cancellationToken)
    {
        var diagnostics = tab.Diagnostics.Where(m => m.Start <= end && m.End >= start).Select(m => m.Diagnostic).ToList();
        var range = new TextRange(tab.Document.GetPosition(start), tab.Document.GetPosition(end));
        return RequestAsync(tab, c => c.HasCodeActions, (client, path) => client.CodeActionsAsync(path, range, diagnostics, cancellationToken));
    }

    /// <summary>Runs a code action's server command for the tab's server (its edits come back through <see cref="EditRequested"/>).</summary>
    public async Task ExecuteCommandAsync(EditorTabViewModel tab, LspCommand command)
    {
        if (ReadyDocument(tab) is { Session.Client: { } client })
        {
            await Safely(async () =>
            {
                await client.ExecuteCommandAsync(command, CancellationToken.None);
                return string.Empty;
            });
        }
    }

    /// <summary>The symbols declared in a document (Go to Symbol in Editor).</summary>
    public Task<IReadOnlyList<DocumentSymbol>?> DocumentSymbolsAsync(EditorTabViewModel tab, CancellationToken cancellationToken) =>
        RequestAsync(tab, c => c.HasDocumentSymbols, (client, path) => client.DocumentSymbolsAsync(path, cancellationToken));

    /// <summary>Inlay hints for a range of lines of a document.</summary>
    public Task<IReadOnlyList<InlayHint>?> InlayHintsAsync(EditorTabViewModel tab, int firstLine, int lastLine, CancellationToken cancellationToken)
    {
        var last = Math.Min(lastLine, tab.Document.LineCount - 1);
        var range = new TextRange(new TextPosition(firstLine, 0), new TextPosition(last, tab.Document.GetLineLength(last)));
        return RequestAsync(tab, c => c.HasInlayHints, (client, path) => client.InlayHintsAsync(path, range, cancellationToken));
    }

    /// <summary>Whether any running server can search the workspace's symbols.</summary>
    public bool HasWorkspaceSymbols => _sessions.Any(s => s.IsRunning && s.Client!.Capabilities.HasWorkspaceSymbols);

    /// <summary>
    /// Symbols matching <paramref name="query"/> from every running server that can search the workspace
    /// (a project may use several languages). Servers that fail are left out.
    /// </summary>
    public async Task<IReadOnlyList<SymbolInformation>> WorkspaceSymbolsAsync(string query, CancellationToken cancellationToken)
    {
        var clients = _sessions.Where(s => s.IsRunning && s.Client!.Capabilities.HasWorkspaceSymbols).Select(s => s.Client!).ToList();
        foreach (var document in _documents.Values.Where(d => d.IsOpen))
        {
            Flush(document);
        }

        var results = await Task.WhenAll(clients.Select(client => Safely(() => client.WorkspaceSymbolsAsync(query, cancellationToken))));
        return results.OfType<IReadOnlyList<SymbolInformation>>().SelectMany(list => list).ToList();
    }

    /// <summary>A server's inlay hints changed: editors ask for them again.</summary>
    public event EventHandler? InlayHintsChanged;

    /// <summary>Flushes pending changes, then sends a request when the server supports it; null when it does not or fails.</summary>
    private async Task<T?> RequestAsync<T>(EditorTabViewModel tab, Func<ServerCapabilities, bool> supported, Func<LanguageClient, string, Task<T>> request)
    {
        if (ReadyDocument(tab) is not { Session.Client: { } client } document || !supported(client.Capabilities))
        {
            return default;
        }

        Flush(document);
        return await Safely(() => request(client, document.Path));
    }

    /// <summary>Tells every running server that these files changed on disk (they were not open in an editor).</summary>
    public void NotifyFilesChanged(IReadOnlyList<string> paths) =>
        NotifyFilesChanged(paths.Select(path => (path, FileChangeType.Changed)).ToList());

    /// <summary>Tells every running server that files were created, changed or deleted on disk.</summary>
    public void NotifyFilesChanged(IReadOnlyList<(string Path, FileChangeType Type)> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        foreach (var session in _sessions.Where(s => s.IsRunning))
        {
            session.Client!.DidChangeWatchedFiles(files);
        }
    }

    /// <summary>Stops every server and starts fresh ones for the open documents.</summary>
    public async Task RestartAsync()
    {
        var tabs = TrackedTabs();
        foreach (var tab in tabs)
        {
            Untrack(tab);
        }

        await StopAllAsync();
        _reportedMissing.Clear();
        foreach (var tab in tabs)
        {
            Track(tab);
        }
    }

    /// <summary>The servers started so far, with their state ("tailwindcss-language-server: running").</summary>
    public IReadOnlyList<string> ServerStates => _sessions.Select(s => $"{s.Name}: {s.State.ToString().ToLowerInvariant()}").ToList();

    /// <summary>Every tab connected to a server, its main one or a companion.</summary>
    private List<EditorTabViewModel> TrackedTabs() => _documents.Keys.Union(_companions.Keys).ToList();

    /// <summary>Stops every server. Safe to call more than once: later calls wait for the same shutdown.</summary>
    public ValueTask DisposeAsync() => new(_disposal ??= ShutdownAsync());

    private async Task ShutdownAsync()
    {
        _editors.Tabs.CollectionChanged -= OnTabsChanged;
        foreach (var tab in TrackedTabs())
        {
            Untrack(tab);
        }

        await StopAllAsync();
    }

    // Tracking documents ------------------------------------------------------------------------

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var tab in e.OldItems?.OfType<EditorTabViewModel>() ?? [])
        {
            Untrack(tab);
        }

        foreach (var tab in e.NewItems?.OfType<EditorTabViewModel>() ?? [])
        {
            Track(tab);
        }
    }

    private void Track(EditorTabViewModel tab)
    {
        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.PropertyChanged += OnTabPropertyChanged; // Save As changes the path, and maybe the server
        if (tab.FilePath is not { } path || _launcher is null)
        {
            return;
        }

        // The file's main server.
        if (!_documents.ContainsKey(tab) && LanguageServerCatalog.For(tab.Language.Id, _customServers()) is { } server && IsReady(server, tab, report: true))
        {
            var document = _documents[tab] = Attach(tab, path, server);
            _ = OpenWhenReadyAsync(document);
            if (_diagnostics.TryGetValue(path, out var known))
            {
                tab.SetDiagnostics(known);
            }

            OnPropertyChanged(nameof(StatusText));
        }

        // Servers beside it (Tailwind), for projects that use them.
        foreach (var (companion, usedBy) in LanguageServerCatalog.Companions.Where(c => c.Server.LanguageIds.Contains(tab.Language.Id)))
        {
            var attached = _companions.TryGetValue(tab, out var list) ? list : _companions[tab] = [];
            if (attached.Any(d => d.Session.Server == companion) || !usedBy(LanguageServerCatalog.FindRoot(companion, path, _workspaceFolder())))
            {
                continue;
            }

            if (IsReady(companion, tab, report: false))
            {
                var document = Attach(tab, path, companion);
                attached.Add(document);
                _ = OpenWhenReadyAsync(document);
            }
        }
    }

    /// <summary>
    /// Whether the server can start now. A server Coder ships but hasn't installed yet is installed (then
    /// the tab is tracked again); one that has to be installed by hand is explained once.
    /// </summary>
    private bool IsReady(LanguageServerDefinition server, EditorTabViewModel tab, bool report)
    {
        if (_launcher!.IsInstalled(server))
        {
            return true;
        }

        if (_launcher.CanInstall(server))
        {
            _ = InstallAsync(server);
        }
        else if (report && _reportedMissing.Add(server.Name))
        {
            Notice?.Invoke(this, $"Install {server.Name} for {tab.Language.DisplayName} completions and diagnostics: {server.InstallHint}");
        }

        return false;
    }

    /// <summary>Connects a document to a server for its project root (started if needed); the caller opens it there.</summary>
    private TrackedDocument Attach(EditorTabViewModel tab, string path, LanguageServerDefinition server)
    {
        var root = LanguageServerCatalog.FindRoot(server, path, _workspaceFolder());
        var session = _sessions.FirstOrDefault(s => s.Server == server && DocumentUri.PathComparer.Equals(s.Root, root))
                      ?? StartSession(server, root);
        var document = new TrackedDocument(tab, path, session);
        tab.Document.Changed += document.OnChanged;
        document.ChangeRecorded += (_, _) => ScheduleFlush(document);
        return document; // the caller registers it, then opens it (OpenWhenReadyAsync checks it is registered)
    }

    /// <summary>Stops sending a document's changes to its server and closes it there.</summary>
    private static void Detach(TrackedDocument document)
    {
        document.Tab.Document.Changed -= document.OnChanged;
        if (document.IsOpen && document.Session.IsRunning)
        {
            Flush(document);
            document.Session.Client!.DidClose(document.Path);
        }

        document.IsOpen = false;
    }

    /// <summary>Whether a document is still connected (its tab may have closed while its server started).</summary>
    private bool IsTracked(TrackedDocument document) =>
        _documents.TryGetValue(document.Tab, out var main) && main == document
        || (_companions.TryGetValue(document.Tab, out var companions) && companions.Contains(document));

    /// <summary>A tab's companion documents that are open on a running server.</summary>
    private IEnumerable<TrackedDocument> ReadyCompanions(EditorTabViewModel tab) =>
        _companions.TryGetValue(tab, out var list) ? list.Where(d => d.IsOpen && d.Session.IsRunning) : [];

    /// <summary>
    /// Installs a server Coder ships (once, even when several files ask), then starts it for every open
    /// document it serves. The status bar shows the progress, and why it failed if it did.
    /// </summary>
    private Task InstallAsync(LanguageServerDefinition server)
    {
        if (!_installs.TryGetValue(server.Name, out var install))
        {
            _installs[server.Name] = install = RunInstallAsync(server);
        }

        return install;
    }

    private async Task RunInstallAsync(LanguageServerDefinition server)
    {
        var name = server.Tool?.DisplayName ?? server.Name;
        Installing = $"Installing {name}…";
        Notice?.Invoke(this, $"Installing {name} (first use)…");
        var error = await _launcher!.InstallAsync(server);
        Installing = null;
        if (error is not null)
        {
            Notice?.Invoke(this, $"Couldn't install {name}: {FirstLine(error)}");
            return;
        }

        Notice?.Invoke(this, $"{name} is ready.");
        foreach (var tab in _editors.Tabs.OfType<EditorTabViewModel>().ToList())
        {
            Track(tab); // attaches only what is missing
        }
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? text;

    /// <summary>A server being installed, for the status bar; null when none is.</summary>
    private string? Installing
    {
        get => _installing;
        set
        {
            _installing = value;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private void Untrack(EditorTabViewModel tab)
    {
        tab.PropertyChanged -= OnTabPropertyChanged;
        if (_companions.Remove(tab, out var companions))
        {
            companions.ForEach(Detach);
        }

        if (!_documents.Remove(tab, out var document))
        {
            return;
        }

        Detach(document);
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorTabViewModel.FilePath) && sender is EditorTabViewModel tab
            && (!_documents.TryGetValue(tab, out var document) || !DocumentUri.PathComparer.Equals(document.Path, tab.FilePath)))
        {
            Untrack(tab);
            Track(tab);
        }
    }

    private async Task OpenWhenReadyAsync(TrackedDocument document)
    {
        if (!await document.Session.WaitUntilReadyAsync() || !IsTracked(document))
        {
            return;
        }

        var tab = document.Tab;
        document.Session.Client!.DidOpen(document.Path, LspLanguageId(tab.Language.Id, document.Path), tab.Document.Version, tab.Document.GetText());
        document.IsOpen = true;
        document.SyncKind = document.Session.Client.Capabilities.TextSync;
    }

    /// <summary>
    /// The language id a server expects: Coder groups .tsx with TypeScript and .scss with CSS, but servers
    /// tell them apart (JSX only parses as "typescriptreact").
    /// </summary>
    internal static string LspLanguageId(string languageId, string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".tsx" => "typescriptreact",
        ".jsx" => "javascriptreact",
        ".scss" => "scss",
        ".less" => "less",
        ".jsonc" => "jsonc",
        _ => languageId,
    };

    private void OnSaved(EditorTabViewModel tab)
    {
        if (_documents.TryGetValue(tab, out var document) && document.IsOpen && document.Session.IsRunning)
        {
            Flush(document);
            document.Session.Client!.DidSave(document.Path, tab.Document.GetText());
        }
    }

    /// <summary>Changes are batched and sent once per UI turn, so a multi-cursor edit is one notification.</summary>
    private void ScheduleFlush(TrackedDocument document)
    {
        if (document.FlushScheduled)
        {
            return;
        }

        document.FlushScheduled = true;
        Post(() => Flush(document));
    }

    private static void Flush(TrackedDocument document)
    {
        document.FlushScheduled = false;
        if (document.TakePendingChanges() is { } changes && document.Session.IsRunning)
        {
            document.Session.Client!.DidChange(document.Path, document.Tab.Document.Version, changes);
        }
    }

    private TrackedDocument? ReadyDocument(EditorTabViewModel tab) =>
        _documents.TryGetValue(tab, out var document) && document.IsOpen && document.Session.IsRunning ? document : null;

    // Servers -----------------------------------------------------------------------------------

    private ServerSession StartSession(LanguageServerDefinition server, string root)
    {
        var session = new ServerSession(server, root);
        _sessions.Add(session);
        try
        {
            var client = _launcher!.Launch(server, root);
            session.Client = client;
            client.DiagnosticsPublished += (_, report) => Post(() => OnDiagnostics(session, report));
            client.MessageShown += (_, message) => Post(() => Notice?.Invoke(this, message));
            client.ApplyEditRequested += (_, edit) => Post(() => EditRequested?.Invoke(this, edit));
            client.InlayHintsRefreshRequested += (_, _) => Post(() => InlayHintsChanged?.Invoke(this, EventArgs.Empty));
            client.ProgressChanged += (_, progress) => Post(() =>
            {
                session.Progress = progress;
                OnPropertyChanged(nameof(StatusText));
            });
            client.Stopped += (_, error) => Post(() => OnStopped(session, error));
            session.Ready = InitializeAsync(session, client);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            session.State = SessionState.Stopped;
            session.Ready = Task.FromResult(false);
            Notice?.Invoke(this, $"Couldn't start {server.Name}: {exception.Message}");
        }

        return session;
    }

    private async Task<bool> InitializeAsync(ServerSession session, LanguageClient client)
    {
        try
        {
            using var timeout = new CancellationTokenSource(StartupTimeout);
            await client.InitializeAsync(timeout.Token);
            session.State = SessionState.Running;
            return true;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or LanguageServerException or ObjectDisposedException)
        {
            session.State = SessionState.Stopped;
            Notice?.Invoke(this, $"{session.Name} failed to start: {Explain(exception, client)}");
            return false;
        }
        finally
        {
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private void OnStopped(ServerSession session, Exception? error)
    {
        var wasRunning = session.State == SessionState.Running;
        session.State = SessionState.Stopped;
        session.Progress = null;
        if (wasRunning && !session.IsStopping)
        {
            Notice?.Invoke(this, $"{session.Name} stopped unexpectedly. {Explain(error, session.Client)} Run \"Restart Language Servers\" to start it again.");
        }

        OnPropertyChanged(nameof(StatusText));
    }

    private async Task StopAllAsync()
    {
        var sessions = _sessions.ToList();
        _sessions.Clear();
        await Task.WhenAll(sessions.Select(async session =>
        {
            session.IsStopping = true;
            if (session.Client is { } client)
            {
                await client.DisposeAsync();
            }
        }));

        _diagnostics.Clear();
        _diagnosticsByServer.Clear();
        UpdateCounts();
    }

    /// <summary>
    /// A server replaced its problems for a file. Several servers can report on one file (TypeScript and
    /// Tailwind on a .tsx): each server's list is kept, and the file shows them together.
    /// </summary>
    private void OnDiagnostics(ServerSession session, DiagnosticsReport report)
    {
        if (!_diagnosticsByServer.TryGetValue(report.Path, out var byServer))
        {
            _diagnosticsByServer[report.Path] = byServer = [];
        }

        byServer[session] = report.Diagnostics;
        var merged = byServer.Values.SelectMany(list => list).ToList();
        if (merged.Count == 0)
        {
            _diagnostics.Remove(report.Path);
            _diagnosticsByServer.Remove(report.Path);
        }
        else
        {
            _diagnostics[report.Path] = merged;
        }

        if (_editors.FindTab(report.Path) is { } tab)
        {
            tab.SetDiagnostics(merged);
        }

        UpdateCounts();
    }

    private void UpdateCounts()
    {
        var all = _diagnostics.Values.SelectMany(list => list).ToList();
        ErrorCount = all.Count(d => d.Severity == DiagnosticSeverity.Error);
        WarningCount = all.Count(d => d.Severity == DiagnosticSeverity.Warning);
        DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Explain(Exception? error, LanguageClient? client)
    {
        var detail = client?.RecentErrorOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        return detail ?? error?.Message ?? string.Empty;
    }

    /// <summary>Request failures (server busy, crashed, cancelled) mean "no answer", never an error dialog.</summary>
    private static async Task<T?> Safely<T>(Func<Task<T>> request)
    {
        try
        {
            return await request();
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or LanguageServerException or ObjectDisposedException)
        {
            return default;
        }
    }

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }

    private enum SessionState
    {
        Starting,
        Running,
        Stopped,
    }

    /// <summary>One running server for one project root.</summary>
    private sealed class ServerSession(LanguageServerDefinition server, string root)
    {
        public LanguageServerDefinition Server { get; } = server;

        public string Root { get; } = root;

        public string Name => Server.Name;

        public LanguageClient? Client { get; set; }

        public Task<bool> Ready { get; set; } = Task.FromResult(false);

        public SessionState State { get; set; } = SessionState.Starting;

        public bool IsRunning => State == SessionState.Running;

        public bool IsStopping { get; set; }

        public string? Progress { get; set; }

        public Task<bool> WaitUntilReadyAsync() => Ready;
    }

    /// <summary>An open document and the edits not yet sent to its server.</summary>
    private sealed class TrackedDocument(EditorTabViewModel tab, string path, ServerSession session)
    {
        private List<ContentChange>? _pending;
        private bool _needsFullText;

        public event EventHandler? ChangeRecorded;

        public EditorTabViewModel Tab { get; } = tab;

        public string Path { get; } = path;

        public ServerSession Session { get; } = session;

        public bool IsOpen { get; set; }

        public bool FlushScheduled { get; set; }

        public TextSyncKind SyncKind { get; set; }

        /// <summary>
        /// Records one change. Its range is in the text before the change: the start line is unchanged
        /// by the edit, and the end follows from the removed text.
        /// </summary>
        public void OnChanged(object? sender, TextChange change)
        {
            if (!IsOpen || SyncKind == TextSyncKind.None)
            {
                return; // not opened yet: didOpen will carry the current text
            }

            if (SyncKind == TextSyncKind.Full)
            {
                _needsFullText = true;
            }
            else
            {
                var document = Tab.Document;
                var start = new TextPosition(change.StartLine, change.Offset - document.GetLineStart(change.StartLine));
                var removed = change.RemovedText;
                var lastBreak = removed.LastIndexOf('\n');
                var end = lastBreak < 0
                    ? start with { Column = start.Column + removed.Length }
                    : new TextPosition(start.Line + change.RemovedLineBreaks, removed.Length - lastBreak - 1);
                (_pending ??= []).Add(new ContentChange(new TextRange(start, end), change.InsertedText));
            }

            ChangeRecorded?.Invoke(this, EventArgs.Empty);
        }

        public IReadOnlyList<ContentChange>? TakePendingChanges()
        {
            IReadOnlyList<ContentChange>? changes = _needsFullText ? [new ContentChange(null, Tab.Document.GetText())] : _pending;
            _pending = null;
            _needsFullText = false;
            return changes;
        }
    }
}
