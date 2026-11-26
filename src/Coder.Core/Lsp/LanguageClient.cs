using System.Text.Json;
using System.Text.Json.Nodes;
using Coder.Core.Text;

namespace Coder.Core.Lsp;

public enum LanguageClientState
{
    Starting,
    Running,
    Stopped,
}

/// <summary>Diagnostics a server published for one file.</summary>
public sealed record DiagnosticsReport(string Path, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>The pipes to a running language server, and whatever must be cleaned up with them (a process).</summary>
public interface ILanguageServerTransport : IDisposable
{
    /// <summary>What the server writes (its stdout).</summary>
    Stream Input { get; }

    /// <summary>What the server reads (its stdin).</summary>
    Stream Output { get; }

    /// <summary>The last lines the server wrote to stderr, to explain a failure.</summary>
    string RecentErrorOutput { get; }
}

/// <summary>
/// One connection to a language server: the initialize/shutdown lifecycle, document sync
/// notifications and the requests the editor uses (completion, hover, definition).
/// <para>Events are raised on a background thread; the caller marshals to its UI thread.</para>
/// </summary>
public sealed class LanguageClient : IAsyncDisposable
{
    private readonly ILanguageServerTransport _transport;
    private readonly JsonRpcConnection _connection;
    private readonly string _rootPath;

    public LanguageClient(ILanguageServerTransport transport, string rootPath, string name)
    {
        _transport = transport;
        _rootPath = rootPath;
        Name = name;
        _connection = new JsonRpcConnection(transport.Input, transport.Output);
        _connection.Closed += (_, error) =>
        {
            State = LanguageClientState.Stopped;
            Stopped?.Invoke(this, error);
        };
        RegisterServerMessages();
    }

    public string Name { get; }

    public string RootPath => _rootPath;

    public LanguageClientState State { get; private set; } = LanguageClientState.Starting;

    public ServerCapabilities Capabilities { get; private set; } = new();

    /// <summary>The server's settings as JSON keyed by section (see <see cref="LanguageServerDefinition.Settings"/>).</summary>
    public string? Settings { get; init; }

    /// <summary>Options sent with <c>initialize</c> (JSON), see <see cref="LanguageServerDefinition.InitializationOptions"/>.</summary>
    public string? InitializationOptions { get; init; }

    public string RecentErrorOutput => _transport.RecentErrorOutput;

    public event EventHandler<DiagnosticsReport>? DiagnosticsPublished;

    /// <summary>A message the server wants the user to see (<c>window/showMessage</c>).</summary>
    public event EventHandler<string>? MessageShown;

    /// <summary>Work in progress ("Loading packages…"), or null when the server is idle.</summary>
    public event EventHandler<string?>? ProgressChanged;

    /// <summary>The server asks the editor to apply edits (<c>workspace/applyEdit</c>).</summary>
    public event EventHandler<WorkspaceEdit>? ApplyEditRequested;

    /// <summary>The connection ended; the argument is the error, if any.</summary>
    public event EventHandler<Exception?>? Stopped;

    /// <summary>The server's inlay hints changed (after a build or settings change): ask for them again.</summary>
    public event EventHandler? InlayHintsRefreshRequested;

    /// <summary>Starts the conversation: <c>initialize</c>, then <c>initialized</c>.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _connection.Start();
        var result = await _connection.RequestAsync("initialize", InitializeParams(), cancellationToken).ConfigureAwait(false);
        Capabilities = LspJson.ReadCapabilities(result);
        _connection.Notify("initialized", new JsonObject());
        State = LanguageClientState.Running;
    }

    // Document sync -----------------------------------------------------------------------------

    public void DidOpen(string path, string languageId, int version, string text) =>
        _connection.Notify("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = DocumentUri.FromPath(path),
                ["languageId"] = languageId,
                ["version"] = version,
                ["text"] = text,
            },
        });

    public void DidChange(string path, int version, IReadOnlyList<ContentChange> changes)
    {
        var array = new JsonArray();
        foreach (var change in changes)
        {
            var entry = new JsonObject();
            if (change.Range is { } range)
            {
                entry["range"] = LspJson.Range(range);
            }

            entry["text"] = change.Text;
            array.Add(entry);
        }

        _connection.Notify("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = DocumentUri.FromPath(path), ["version"] = version },
            ["contentChanges"] = array,
        });
    }

    public void DidSave(string path, string text)
    {
        var parameters = new JsonObject { ["textDocument"] = LspJson.DocumentId(path) };
        if (Capabilities.SendsSaveText)
        {
            parameters["text"] = text;
        }

        _connection.Notify("textDocument/didSave", parameters);
    }

    /// <summary>Tells the server that files changed on disk behind its back (edits to closed files, checkouts).</summary>
    public void DidChangeWatchedFiles(IEnumerable<string> paths) =>
        DidChangeWatchedFiles(paths.Select(path => (path, FileChangeType.Changed)));

    /// <summary>Tells the server that files were created, changed or deleted on disk.</summary>
    public void DidChangeWatchedFiles(IEnumerable<(string Path, FileChangeType Type)> files)
    {
        var changes = new JsonArray();
        foreach (var (path, type) in files)
        {
            changes.Add(new JsonObject { ["uri"] = DocumentUri.FromPath(path), ["type"] = (int)type });
        }

        _connection.Notify("workspace/didChangeWatchedFiles", new JsonObject { ["changes"] = changes });
    }

    public void DidClose(string path) =>
        _connection.Notify("textDocument/didClose", new JsonObject { ["textDocument"] = LspJson.DocumentId(path) });

    // Requests ----------------------------------------------------------------------------------

    /// <param name="triggerCharacter">The character that triggered completion, or null when invoked by typing a word or Ctrl+Space.</param>
    public async Task<CompletionList> CompletionAsync(string path, TextPosition position, string? triggerCharacter, CancellationToken cancellationToken)
    {
        var parameters = LspJson.PositionParams(path, position);
        parameters["context"] = triggerCharacter is null
            ? new JsonObject { ["triggerKind"] = 1 }
            : new JsonObject { ["triggerKind"] = 2, ["triggerCharacter"] = triggerCharacter };
        var result = await _connection.RequestAsync("textDocument/completion", parameters, cancellationToken).ConfigureAwait(false);
        return LspJson.ReadCompletion(result);
    }

    public async Task<IReadOnlyList<Location>> DefinitionAsync(string path, TextPosition position, CancellationToken cancellationToken)
    {
        var result = await _connection.RequestAsync("textDocument/definition", LspJson.PositionParams(path, position), cancellationToken).ConfigureAwait(false);
        return LspJson.ReadLocations(result);
    }

    public async Task<HoverInfo?> HoverAsync(string path, TextPosition position, CancellationToken cancellationToken)
    {
        var result = await _connection.RequestAsync("textDocument/hover", LspJson.PositionParams(path, position), cancellationToken).ConfigureAwait(false);
        return LspJson.ReadHover(result);
    }

    public async Task<IReadOnlyList<Location>> ReferencesAsync(string path, TextPosition position, CancellationToken cancellationToken)
    {
        var parameters = LspJson.PositionParams(path, position);
        parameters["context"] = new JsonObject { ["includeDeclaration"] = true };
        var result = await _connection.RequestAsync("textDocument/references", parameters, cancellationToken).ConfigureAwait(false);
        return LspJson.ReadLocations(result);
    }

    public async Task<WorkspaceEdit?> RenameAsync(string path, TextPosition position, string newName, CancellationToken cancellationToken)
    {
        var parameters = LspJson.PositionParams(path, position);
        parameters["newName"] = newName;
        var result = await _connection.RequestAsync("textDocument/rename", parameters, cancellationToken).ConfigureAwait(false);
        return LspJson.ReadWorkspaceEdit(result);
    }

    /// <param name="triggerCharacter">The typed character that asked for help ("(" or ","), or null when invoked.</param>
    public async Task<SignatureHelp?> SignatureHelpAsync(string path, TextPosition position, string? triggerCharacter, CancellationToken cancellationToken)
    {
        var parameters = LspJson.PositionParams(path, position);
        parameters["context"] = triggerCharacter is null
            ? new JsonObject { ["triggerKind"] = 1, ["isRetrigger"] = false }
            : new JsonObject { ["triggerKind"] = 2, ["triggerCharacter"] = triggerCharacter, ["isRetrigger"] = false };
        var result = await _connection.RequestAsync("textDocument/signatureHelp", parameters, cancellationToken).ConfigureAwait(false);
        return LspJson.ReadSignatureHelp(result);
    }

    public async Task<IReadOnlyList<TextReplacement>> FormattingAsync(string path, int tabSize, bool insertSpaces, CancellationToken cancellationToken)
    {
        var parameters = new JsonObject
        {
            ["textDocument"] = LspJson.DocumentId(path),
            ["options"] = new JsonObject { ["tabSize"] = tabSize, ["insertSpaces"] = insertSpaces, ["trimTrailingWhitespace"] = true, ["insertFinalNewline"] = true },
        };
        var result = await _connection.RequestAsync("textDocument/formatting", parameters, cancellationToken).ConfigureAwait(false);
        return LspJson.ReadTextEdits(result);
    }

    /// <summary>Quick fixes and refactorings for a range, given the diagnostics there (sent back as the server wrote them).</summary>
    public async Task<IReadOnlyList<CodeAction>> CodeActionsAsync(string path, TextRange range, IReadOnlyList<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var diagnosticsJson = new JsonArray();
        foreach (var diagnostic in diagnostics)
        {
            diagnosticsJson.Add(diagnostic.Raw is { } raw
                ? JsonNode.Parse(raw.GetRawText())
                : new JsonObject { ["range"] = LspJson.Range(diagnostic.Range), ["message"] = diagnostic.Message, ["severity"] = (int)diagnostic.Severity });
        }

        var parameters = new JsonObject
        {
            ["textDocument"] = LspJson.DocumentId(path),
            ["range"] = LspJson.Range(range),
            ["context"] = new JsonObject { ["diagnostics"] = diagnosticsJson, ["triggerKind"] = 1 },
        };
        var result = await _connection.RequestAsync("textDocument/codeAction", parameters, cancellationToken).ConfigureAwait(false);
        return LspJson.ReadCodeActions(result);
    }

    /// <summary>Symbols whose names match <paramref name="query"/> (fuzzily, as the server sees fit) anywhere in the workspace.</summary>
    public async Task<IReadOnlyList<SymbolInformation>> WorkspaceSymbolsAsync(string query, CancellationToken cancellationToken)
    {
        var result = await _connection.RequestAsync("workspace/symbol", new JsonObject { ["query"] = query }, cancellationToken).ConfigureAwait(false);
        return LspJson.ReadWorkspaceSymbols(result);
    }

    /// <summary>The symbols declared in a document, as a tree (classes containing methods…).</summary>
    public async Task<IReadOnlyList<DocumentSymbol>> DocumentSymbolsAsync(string path, CancellationToken cancellationToken)
    {
        var result = await _connection.RequestAsync("textDocument/documentSymbol", new JsonObject { ["textDocument"] = LspJson.DocumentId(path) }, cancellationToken).ConfigureAwait(false);
        return LspJson.ReadDocumentSymbols(result);
    }

    /// <summary>Inlay hints (parameter names, inferred types) for a range of a document.</summary>
    public async Task<IReadOnlyList<InlayHint>> InlayHintsAsync(string path, TextRange range, CancellationToken cancellationToken)
    {
        var parameters = new JsonObject { ["textDocument"] = LspJson.DocumentId(path), ["range"] = LspJson.Range(range) };
        var result = await _connection.RequestAsync("textDocument/inlayHint", parameters, cancellationToken).ConfigureAwait(false);
        return LspJson.ReadInlayHints(result);
    }

    /// <summary>Runs a server command. Any edits it makes arrive through <see cref="ApplyEditRequested"/>.</summary>
    public Task ExecuteCommandAsync(LspCommand command, CancellationToken cancellationToken) =>
        _connection.RequestAsync("workspace/executeCommand", LspJson.CommandJson(command), cancellationToken);

    // Lifecycle ---------------------------------------------------------------------------------

    /// <summary>Asks the server to shut down and exit, then ends the connection and the process.</summary>
    public async ValueTask DisposeAsync()
    {
        if (State == LanguageClientState.Running)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _connection.RequestAsync("shutdown", null, timeout.Token).ConfigureAwait(false);
                _connection.Notify("exit", null);
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false); // let "exit" reach the pipe
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or LanguageServerException)
            {
                // The server is unresponsive or already gone; disposing the transport ends it.
            }
        }

        _connection.Dispose();
        _transport.Dispose();
        State = LanguageClientState.Stopped;
    }

    private JsonObject InitializeParams() => new()
    {
        ["initializationOptions"] = InitializationOptions is null ? null : JsonNode.Parse(InitializationOptions),
        ["processId"] = Environment.ProcessId,
        ["clientInfo"] = new JsonObject { ["name"] = "Coder" },
        ["rootUri"] = DocumentUri.FromPath(_rootPath),
        ["rootPath"] = _rootPath,
        ["workspaceFolders"] = WorkspaceFolders(),
        ["capabilities"] = new JsonObject
        {
            ["general"] = new JsonObject { ["positionEncodings"] = new JsonArray("utf-16") },
            ["window"] = new JsonObject { ["workDoneProgress"] = true, ["showMessage"] = new JsonObject() },
            ["workspace"] = new JsonObject
            {
                ["configuration"] = true,
                ["workspaceFolders"] = true,
                ["applyEdit"] = true,
                ["didChangeWatchedFiles"] = new JsonObject { ["dynamicRegistration"] = false },
                ["workspaceEdit"] = new JsonObject
                {
                    ["documentChanges"] = true,
                    ["resourceOperations"] = new JsonArray("create", "rename", "delete"),
                    ["failureHandling"] = "abort",
                },
                ["symbol"] = new JsonObject
                {
                    ["symbolKind"] = new JsonObject { ["valueSet"] = new JsonArray(Enumerable.Range(1, 26).Select(i => (JsonNode)i).ToArray()) },
                },
                ["inlayHint"] = new JsonObject { ["refreshSupport"] = true },
            },
            ["textDocument"] = new JsonObject
            {
                ["synchronization"] = new JsonObject { ["didSave"] = true, ["dynamicRegistration"] = false },
                ["completion"] = new JsonObject
                {
                    ["contextSupport"] = true,
                    ["completionItem"] = new JsonObject
                    {
                        ["snippetSupport"] = true,
                        ["documentationFormat"] = new JsonArray("markdown", "plaintext"),
                        ["labelDetailsSupport"] = true,
                        ["preselectSupport"] = true,
                    },
                    ["completionItemKind"] = new JsonObject { ["valueSet"] = new JsonArray(Enumerable.Range(1, 25).Select(i => (JsonNode)i).ToArray()) },
                },
                ["hover"] = new JsonObject { ["contentFormat"] = new JsonArray("markdown", "plaintext") },
                ["definition"] = new JsonObject { ["linkSupport"] = true },
                ["references"] = new JsonObject(),
                ["rename"] = new JsonObject { ["prepareSupport"] = false },
                ["formatting"] = new JsonObject(),
                ["signatureHelp"] = new JsonObject
                {
                    ["signatureInformation"] = new JsonObject
                    {
                        ["documentationFormat"] = new JsonArray("markdown", "plaintext"),
                        ["parameterInformation"] = new JsonObject { ["labelOffsetSupport"] = true },
                        ["activeParameterSupport"] = true,
                    },
                },
                ["codeAction"] = new JsonObject
                {
                    ["isPreferredSupport"] = true,
                    ["codeActionLiteralSupport"] = new JsonObject
                    {
                        ["codeActionKind"] = new JsonObject
                        {
                            ["valueSet"] = new JsonArray("", "quickfix", "refactor", "refactor.extract", "refactor.inline", "refactor.rewrite", "source", "source.organizeImports", "source.fixAll"),
                        },
                    },
                },
                ["publishDiagnostics"] = new JsonObject { ["relatedInformation"] = false },
                ["documentSymbol"] = new JsonObject
                {
                    ["hierarchicalDocumentSymbolSupport"] = true,
                    ["symbolKind"] = new JsonObject { ["valueSet"] = new JsonArray(Enumerable.Range(1, 26).Select(i => (JsonNode)i).ToArray()) },
                },
                ["inlayHint"] = new JsonObject(),
            },
        },
    };

    /// <summary>A configuration section ("gopls", or a dotted path such as "typescript.inlayHints") from <see cref="Settings"/>; null when unset.</summary>
    private JsonNode? ConfigurationSection(string? section)
    {
        if (Settings is null)
        {
            return null;
        }

        JsonNode? node = JsonNode.Parse(Settings);
        foreach (var part in (section ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node is JsonObject json && json.TryGetPropertyValue(part, out var child) ? child : null;
        }

        return node?.DeepClone(); // a node can only have one parent: the answer array gets its own copy
    }

    private JsonArray WorkspaceFolders() =>
        [new JsonObject { ["uri"] = DocumentUri.FromPath(_rootPath), ["name"] = Path.GetFileName(_rootPath.TrimEnd(Path.DirectorySeparatorChar)) }];

    /// <summary>Messages the server sends on its own; requests get safe default answers.</summary>
    private void RegisterServerMessages()
    {
        _connection.OnNotification("textDocument/publishDiagnostics", parameters =>
        {
            if (DocumentUri.ToPath(parameters.GetProperty("uri").GetString()) is { } path)
            {
                var diagnostics = parameters.TryGetProperty("diagnostics", out var array) && array.ValueKind == JsonValueKind.Array
                    ? array.EnumerateArray().Select(LspJson.ReadDiagnostic).ToList()
                    : [];
                DiagnosticsPublished?.Invoke(this, new DiagnosticsReport(path, diagnostics));
            }
        });

        _connection.OnNotification("window/showMessage", parameters =>
        {
            // Errors and warnings only; info messages from servers are mostly noise.
            if (parameters.TryGetProperty("type", out var type) && type.GetInt32() <= 2 && parameters.TryGetProperty("message", out var message))
            {
                MessageShown?.Invoke(this, $"{Name}: {message.GetString()}");
            }
        });

        _connection.OnNotification("$/progress", parameters =>
        {
            if (!parameters.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var kind = value.TryGetProperty("kind", out var k) ? k.GetString() : null;
            var title = value.TryGetProperty("title", out var t) ? t.GetString() : null;
            var message = value.TryGetProperty("message", out var m) ? m.GetString() : null;
            ProgressChanged?.Invoke(this, kind == "end" ? null : title ?? message);
        });

        // Every configuration item gets "no setting", which means the server's defaults.
        _connection.OnRequest("workspace/configuration", parameters =>
        {
            var answers = new JsonArray();
            if (parameters.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var section = item.TryGetProperty("section", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    answers.Add(ConfigurationSection(section));
                }
            }

            return answers;
        });
        _connection.OnRequest("workspace/workspaceFolders", _ => WorkspaceFolders());

        // A command the server ran wants to change files (gopls applies many quick fixes this way).
        _connection.OnRequest("workspace/applyEdit", parameters =>
        {
            var edit = parameters.TryGetProperty("edit", out var e) ? LspJson.ReadWorkspaceEdit(e) : null;
            if (edit is not null)
            {
                ApplyEditRequested?.Invoke(this, edit);
            }

            return new JsonObject { ["applied"] = edit is not null };
        });
        _connection.OnRequest("workspace/inlayHint/refresh", _ =>
        {
            InlayHintsRefreshRequested?.Invoke(this, EventArgs.Empty);
            return null;
        });
        _connection.OnRequest("client/registerCapability", _ => null);
        _connection.OnRequest("client/unregisterCapability", _ => null);
        _connection.OnRequest("window/workDoneProgress/create", _ => null);
        _connection.OnRequest("window/showMessageRequest", _ => null);
    }
}
