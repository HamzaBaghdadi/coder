using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coder.Core.Extensions;
using Coder.Core.Files;
using Coder.Core.Lsp;
using Coder.Core.Text;
using Coder.Presentation.Editors;
using Coder.Presentation.Services;
using Coder.Presentation.Workbench;

namespace Coder.Presentation.Extensions;

/// <summary>
/// The Editor API extension programs call (documented in <c>tools/registry/EXTENSION-API.md</c>):
/// folders, open editors and the selection, opening and saving files, a change for the user to accept
/// in a diff, diagnostics, terminals (a shell, or a program run directly), messages and questions with buttons. Positions are zero-based lines and UTF-16 columns,
/// like LSP. It also tells programs when the active editor, the selection or the folder change.
/// Everything here runs on the UI thread (the host marshals requests).
/// </summary>
public sealed class EditorApi : IEditorApi, IDisposable
{
    private static readonly TimeSpan SelectionDelay = TimeSpan.FromMilliseconds(100);
    /// <summary>At most this many buttons in a <c>window/showMessageRequest</c> question.</summary>
    public const int MaxActions = 4;

    private readonly WorkbenchViewModel _workbench;
    private readonly IDialogService _dialogs;
    private readonly DocumentEditApplier _applier;
    private EditorTabViewModel? _tracked;
    private CancellationTokenSource? _selectionDelay;

    public EditorApi(WorkbenchViewModel workbench, IFileSystem fileSystem, IDialogService dialogs)
    {
        _workbench = workbench;
        _dialogs = dialogs;
        _applier = new DocumentEditApplier(workbench.Editors, fileSystem, changes => workbench.Languages.NotifyFilesChanged(changes));
        FileSystem = fileSystem;
        workbench.Editors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveEditor))
            {
                TrackActiveEditor();
            }
        };
        workbench.Explorer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Coder.Presentation.Explorer.ExplorerViewModel.RootPath))
            {
                Notification?.Invoke(this, ("workspace/foldersChanged", new JsonObject { ["folders"] = Folders() }));
            }
        };
        TrackActiveEditor();
    }

    public event EventHandler<(string Method, JsonNode? Parameters)>? Notification;

    private IFileSystem FileSystem { get; }

    public IReadOnlyCollection<string> Methods { get; } =
    [
        "workspace/folders", "editor/open", "editor/list", "editor/selection", "editor/text", "editor/save", "editor/isDirty",
        "diff/review", "diff/close", "diff/closeAll", "diagnostics/get", "terminal/create", "window/showMessage",
        "window/showMessageRequest",
    ];

    public JsonObject InitializeParameters() => new()
    {
        ["coderVersion"] = typeof(EditorApi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
        ["pid"] = Environment.ProcessId,
        ["platform"] = Platform.RuntimeId,
        ["workspaceFolders"] = Folders(),
    };

    public void Dispose() => _selectionDelay?.Dispose();

    public async Task<JsonNode?> HandleAsync(InstalledExtension extension, string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        var p = parameters;
        switch (method)
        {
            case "workspace/folders":
                return new JsonObject { ["folders"] = Folders() };

            case "editor/open":
                return await OpenAsync(Path(p), p.TryGetProperty("range", out var range) ? Range(range) : null, !p.TryGetProperty("focus", out var focus) || focus.ValueKind != JsonValueKind.False);

            case "editor/list":
                return new JsonObject
                {
                    ["editors"] = new JsonArray(_workbench.Editors.Tabs.OfType<EditorTabViewModel>().Select(tab => (JsonNode)new JsonObject
                    {
                        ["path"] = tab.FilePath,
                        ["title"] = tab.Title,
                        ["languageId"] = tab.Language.Id,
                        ["isActive"] = tab.IsActive,
                        ["isDirty"] = tab.IsDirty,
                    }).ToArray()),
                };

            case "editor/selection":
                return _workbench.Editors.ActiveEditor is { } active ? Selection(active) : null;

            case "editor/text":
                return _workbench.Editors.FindTab(Path(p)) is { } open
                    ? new JsonObject { ["text"] = open.Document.GetText(), ["isOpen"] = true }
                    : FileSystem.Exists(Path(p)) ? new JsonObject { ["text"] = (await FileSystem.ReadTextAsync(Path(p), cancellationToken)).Text, ["isOpen"] = false } : null;

            case "editor/save":
                return new JsonObject { ["saved"] = _workbench.Editors.FindTab(Path(p)) is { } toSave && await _workbench.Editors.SaveAsync(toSave) };

            case "editor/isDirty":
                return _workbench.Editors.FindTab(Path(p)) is { } tab
                    ? new JsonObject { ["isOpen"] = true, ["isDirty"] = tab.IsDirty }
                    : new JsonObject { ["isOpen"] = false, ["isDirty"] = false };

            case "diff/review":
                return await ReviewAsync(extension, p, cancellationToken);

            case "diff/close":
                return new JsonObject { ["closed"] = await CloseDiffsAsync(t => t.ReviewName == String(p, "title")) };

            case "diff/closeAll":
                return new JsonObject { ["closed"] = await CloseDiffsAsync(t => t.ModifiedLabel == extension.DisplayName) };

            case "diagnostics/get":
                return Diagnostics(String(p, "path"));

            case "terminal/create":
                var environment = p.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object
                    ? env.EnumerateObject().Where(v => v.Value.ValueKind == JsonValueKind.String).ToDictionary(v => v.Name, v => v.Value.GetString()!)
                    : [];
                var view = String(p, "view");
                if (view is not null && extension.Views.All(v => v.Id != view))
                {
                    throw new ArgumentException($"{view} isn't one of {extension.DisplayName}'s views (coder.views).");
                }

                var (program, arguments) = Program(p);
                _workbench.OpenTerminal(
                    new TerminalRequest(String(p, "name") ?? extension.DisplayName, String(p, "command"), String(p, "cwd"), environment)
                    {
                        View = view,
                        Message = String(p, "message"),
                        Program = program,
                        Arguments = arguments,
                    },
                    replace: p.TryGetProperty("replace", out var replace) && replace.ValueKind == JsonValueKind.True);
                return new JsonObject();

            case "window/showMessage":
                _workbench.StatusBar.Message = String(p, "text") ?? string.Empty;
                return new JsonObject();

            case "window/showMessageRequest":
                return await AskAsync(extension, p, cancellationToken);

            default:
                throw new InvalidOperationException($"Unknown method {method}.");
        }
    }

    // Requests ----------------------------------------------------------------------------------

    private async Task<JsonNode?> OpenAsync(string path, TextRange? range, bool focus)
    {
        var before = _workbench.Editors.ActiveTab;
        if (range is { } r)
        {
            await _workbench.RevealAsync(path, r);
        }
        else
        {
            await _workbench.OpenFileAsync(path);
        }

        var tab = _workbench.Editors.FindTab(path) ?? throw new IOException($"{path} couldn't be opened.");
        if (!focus && before is not null && _workbench.Editors.Tabs.Contains(before))
        {
            _workbench.Editors.ActiveTab = before; // opened behind the current one
        }

        return new JsonObject { ["path"] = tab.FilePath, ["languageId"] = tab.Language.Id, ["lineCount"] = tab.Document.LineCount };
    }

    /// <summary>
    /// Shows the proposal in a diff tab with Accept / Reject and waits. Accepted: the change is written
    /// like a hand edit (open file: one undo step, then saved). The program's cancellation (or another
    /// proposal for the same file) closes it as rejected.
    /// </summary>
    private async Task<JsonNode?> ReviewAsync(InstalledExtension extension, JsonElement p, CancellationToken cancellationToken)
    {
        var path = Path(p);
        var proposed = String(p, "proposed") ?? throw new ArgumentException("diff/review needs \"proposed\".");
        var original = String(p, "original")
                       ?? _workbench.Editors.FindTab(path)?.Document.GetText()
                       ?? (FileSystem.Exists(path) ? (await FileSystem.ReadTextAsync(path, cancellationToken)).Text : null);
        var review = new DiffReview();
        var diff = new DiffTabViewModel(path, original is null ? "New File" : "Current", original ?? string.Empty, extension.DisplayName, proposed)
        {
            Review = review,
            ReviewName = String(p, "title"),
        };
        _workbench.Editors.OpenDiff(diff);

        bool accepted;
        await using (cancellationToken.Register(() => _ = CloseDiffsAsync(t => ReferenceEquals(t, diff))))
        {
            accepted = await review.Decision;
        }

        if (accepted)
        {
            if (!p.TryGetProperty("apply", out var apply) || apply.ValueKind != JsonValueKind.False)
            {
                await _applier.ApplyAsync(path, proposed); // else the program writes it (Claude Code does)
            }

            await CloseDiffsAsync(t => ReferenceEquals(t, diff));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new JsonObject { ["accepted"] = accepted, ["text"] = accepted ? proposed : null };
    }

    /// <summary>
    /// A question with buttons, shaped like LSP's <c>window/showMessageRequest</c>: <c>{message, detail?, actions?: [{title}]}</c>.
    /// Returns the action picked (the same object), or null when dismissed. The first action is the default
    /// button. The extension's name is shown with it, so the user knows who asks.
    /// </summary>
    private async Task<JsonNode?> AskAsync(InstalledExtension extension, JsonElement p, CancellationToken cancellationToken)
    {
        var message = String(p, "message") is { Length: > 0 } m ? m : throw new ArgumentException("window/showMessageRequest needs a \"message\".");
        var actions = new List<JsonElement>();
        if (p.TryGetProperty("actions", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var action in list.EnumerateArray())
            {
                if (action.ValueKind != JsonValueKind.Object || String(action, "title") is not { Length: > 0 })
                {
                    throw new ArgumentException("Each action needs a \"title\".");
                }

                actions.Add(action);
            }
        }

        if (actions.Count > MaxActions)
        {
            throw new ArgumentException($"At most {MaxActions} actions.");
        }

        var detail = String(p, "detail") is { Length: > 0 } d ? $"{d}\n\nFrom {extension.DisplayName}." : $"From {extension.DisplayName}.";
        var titles = actions.Count > 0 ? actions.Select(a => String(a, "title")!).ToList() : ["OK"];
        var picked = await _dialogs.ChooseAsync(message, detail, titles, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return picked is { } index && index < actions.Count ? JsonNode.Parse(actions[index].GetRawText()) : null;
    }

    private async Task<int> CloseDiffsAsync(Func<DiffTabViewModel, bool> which)
    {
        var tabs = _workbench.Editors.Tabs.OfType<DiffTabViewModel>().Where(t => t.Review is not null && which(t)).ToList();
        foreach (var tab in tabs)
        {
            await _workbench.Editors.CloseAsync(tab); // closing rejects a pending one
        }

        return tabs.Count;
    }

    private JsonObject Diagnostics(string? path)
    {
        var files = new JsonArray();
        foreach (var (file, diagnostics) in _workbench.Languages.AllDiagnostics)
        {
            if (path is not null && !DocumentUri.PathComparer.Equals(file, path))
            {
                continue;
            }

            files.Add(new JsonObject
            {
                ["path"] = file,
                ["diagnostics"] = new JsonArray(diagnostics.Select(d => (JsonNode)new JsonObject
                {
                    ["range"] = Range(d.Range.Start, d.Range.End),
                    ["severity"] = d.Severity.ToString().ToLowerInvariant(),
                    ["message"] = d.Message,
                    ["source"] = d.Source,
                    ["code"] = d.Code,
                }).ToArray()),
            });
        }

        return new JsonObject { ["files"] = files };
    }

    // Notifications -----------------------------------------------------------------------------

    private void TrackActiveEditor()
    {
        if (_tracked is not null)
        {
            _tracked.Editor.SelectionChanged -= OnSelectionChanged;
        }

        _tracked = _workbench.Editors.ActiveEditor;
        if (_tracked is not null)
        {
            _tracked.Editor.SelectionChanged += OnSelectionChanged;
        }

        Notification?.Invoke(this, ("editor/activeChanged", new JsonObject { ["path"] = _tracked?.FilePath }));
        OnSelectionChanged(this, EventArgs.Empty);
    }

    /// <summary>Selection changes come with every caret move: tell programs once it settles.</summary>
    private async void OnSelectionChanged(object? sender, EventArgs e)
    {
        _selectionDelay?.Cancel();
        var delay = _selectionDelay = new CancellationTokenSource();
        try
        {
            await Task.Delay(SelectionDelay, delay.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_tracked is { } tab && Notification is not null)
        {
            Notification(this, ("editor/selectionChanged", Selection(tab)));
        }
    }

    // Helpers -----------------------------------------------------------------------------------

    private JsonArray Folders() => _workbench.Explorer.RootPath is { } root ? [root] : [];

    private static JsonObject Selection(EditorTabViewModel tab)
    {
        var selection = tab.Editor.Selection;
        var start = tab.Document.GetPosition(selection.Start);
        var end = tab.Document.GetPosition(selection.End);
        return new JsonObject
        {
            ["path"] = tab.FilePath,
            ["text"] = tab.Editor.SelectedText,
            ["range"] = Range(start, end),
            ["isEmpty"] = selection.IsEmpty,
        };
    }

    private static JsonObject Range(TextPosition start, TextPosition end) => new()
    {
        ["start"] = new JsonObject { ["line"] = start.Line, ["character"] = start.Column },
        ["end"] = new JsonObject { ["line"] = end.Line, ["character"] = end.Column },
    };

    private static TextRange Range(JsonElement range)
    {
        static TextPosition Position(JsonElement p) => new(p.GetProperty("line").GetInt32(), p.GetProperty("character").GetInt32());
        return new TextRange(Position(range.GetProperty("start")), Position(range.GetProperty("end")));
    }

    /// <summary><c>program</c> and <c>args</c> of <c>terminal/create</c>: a program run directly, its arguments passed as given.</summary>
    private static (string? Program, IReadOnlyList<string> Arguments) Program(JsonElement p)
    {
        if (String(p, "program") is not { } program)
        {
            return p.ValueKind == JsonValueKind.Object && p.TryGetProperty("args", out _) ? throw new ArgumentException("\"args\" needs a \"program\".") : (null, []);
        }

        if (program.Length == 0 || program.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("\"program\" must be a program's name or path.");
        }

        if (System.IO.Path.IsPathRooted(program) && !File.Exists(program))
        {
            throw new FileNotFoundException($"{program} doesn't exist.");
        }

        var arguments = new List<string>();
        if (p.TryGetProperty("args", out var args))
        {
            if (args.ValueKind != JsonValueKind.Array || args.EnumerateArray().Any(a => a.ValueKind != JsonValueKind.String))
            {
                throw new ArgumentException("\"args\" must be an array of strings.");
            }

            arguments.AddRange(args.EnumerateArray().Select(a => a.GetString()!));
        }

        return (program, arguments);
    }

    private static string Path(JsonElement p) =>
        String(p, "path") is { Length: > 0 } path && System.IO.Path.IsPathRooted(path)
            ? System.IO.Path.GetFullPath(path)
            : throw new ArgumentException("An absolute \"path\" is needed.");

    private static string? String(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
