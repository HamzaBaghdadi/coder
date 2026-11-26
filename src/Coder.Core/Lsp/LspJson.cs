using System.Text.Json;
using System.Text.Json.Nodes;
using Coder.Core.Text;

namespace Coder.Core.Lsp;

/// <summary>
/// Converts between protocol JSON and the types in <c>LspTypes.cs</c>. Built on <see cref="JsonNode"/> and
/// <see cref="JsonElement"/> rather than reflection, so it stays trim-safe, and tolerant of the
/// variations servers send (strings or objects, single items or arrays).
/// </summary>
internal static class LspJson
{
    public static JsonObject Position(TextPosition position) => new() { ["line"] = position.Line, ["character"] = position.Column };

    public static JsonObject Range(TextRange range) => new() { ["start"] = Position(range.Start), ["end"] = Position(range.End) };

    public static JsonObject DocumentId(string path) => new() { ["uri"] = DocumentUri.FromPath(path) };

    public static JsonObject PositionParams(string path, TextPosition position) =>
        new() { ["textDocument"] = DocumentId(path), ["position"] = Position(position) };

    public static TextPosition ReadPosition(JsonElement element) =>
        new(Int(element, "line"), Int(element, "character"));

    public static TextRange ReadRange(JsonElement element) =>
        new(ReadPosition(element.GetProperty("start")), ReadPosition(element.GetProperty("end")));

    public static Diagnostic ReadDiagnostic(JsonElement element) => new(
        ReadRange(element.GetProperty("range")),
        element.TryGetProperty("severity", out var severity) && severity.ValueKind == JsonValueKind.Number
            ? (DiagnosticSeverity)severity.GetInt32()
            : DiagnosticSeverity.Error,
        String(element, "message") ?? string.Empty,
        String(element, "source"),
        element.TryGetProperty("code", out var code) ? code.ValueKind == JsonValueKind.String ? code.GetString() : code.GetRawText() : null,
        element.Clone());

    public static CompletionList ReadCompletion(JsonElement result)
    {
        // CompletionItem[] | CompletionList | null
        if (result.ValueKind == JsonValueKind.Array)
        {
            return new CompletionList(result.EnumerateArray().Select(ReadCompletionItem).ToList(), IsIncomplete: false);
        }

        if (result.ValueKind != JsonValueKind.Object)
        {
            return new CompletionList([], IsIncomplete: false);
        }

        var items = result.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(ReadCompletionItem).ToList()
            : [];
        return new CompletionList(items, Bool(result, "isIncomplete"));
    }

    public static CompletionItem ReadCompletionItem(JsonElement element)
    {
        TextReplacement? textEdit = null;
        if (element.TryGetProperty("textEdit", out var edit) && edit.ValueKind == JsonValueKind.Object)
        {
            // TextEdit has "range"; InsertReplaceEdit has "insert" and "replace". Typing inserts, like VS Code.
            var range = edit.TryGetProperty("range", out var r) ? r : edit.GetProperty("insert");
            textEdit = new TextReplacement(ReadRange(range), String(edit, "newText") ?? string.Empty);
        }

        return new CompletionItem
        {
            Label = String(element, "label") ?? string.Empty,
            Kind = element.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.Number
                ? (CompletionItemKind)kind.GetInt32()
                : CompletionItemKind.Text,
            Detail = String(element, "detail") ?? LabelDetail(element),
            SortText = String(element, "sortText"),
            FilterText = String(element, "filterText"),
            InsertText = String(element, "insertText"),
            IsSnippet = element.TryGetProperty("insertTextFormat", out var format) && format.ValueKind == JsonValueKind.Number && format.GetInt32() == 2,
            TextEdit = textEdit,
            AdditionalTextEdits = ReadEdits(element, "additionalTextEdits"),
            Preselect = Bool(element, "preselect"),
        };
    }

    /// <summary>Location | Location[] | LocationLink[] | null.</summary>
    public static IReadOnlyList<Location> ReadLocations(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object)
        {
            return ReadLocation(result) is { } single ? [single] : [];
        }

        return result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray().Select(ReadLocation).OfType<Location>().ToList()
            : [];
    }

    public static HoverInfo? ReadHover(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("contents", out var contents))
        {
            return null;
        }

        TextRange? range = result.TryGetProperty("range", out var r) && r.ValueKind == JsonValueKind.Object ? ReadRange(r) : null;
        var (text, isMarkdown) = ReadMarkup(contents);
        return string.IsNullOrWhiteSpace(text) ? null : new HoverInfo(text, isMarkdown, range);
    }

    public static ServerCapabilities ReadCapabilities(JsonElement result)
    {
        if (!result.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Object)
        {
            return new ServerCapabilities();
        }

        var sync = TextSyncKind.Full;
        var saveText = false;
        if (caps.TryGetProperty("textDocumentSync", out var syncElement))
        {
            if (syncElement.ValueKind == JsonValueKind.Number)
            {
                sync = (TextSyncKind)syncElement.GetInt32();
            }
            else if (syncElement.ValueKind == JsonValueKind.Object)
            {
                sync = syncElement.TryGetProperty("change", out var change) && change.ValueKind == JsonValueKind.Number
                    ? (TextSyncKind)change.GetInt32()
                    : TextSyncKind.None;
                saveText = syncElement.TryGetProperty("save", out var save) && save.ValueKind == JsonValueKind.Object && Bool(save, "includeText");
            }
        }

        var triggers = caps.TryGetProperty("completionProvider", out var completion)
                       && completion.ValueKind == JsonValueKind.Object
                       && completion.TryGetProperty("triggerCharacters", out var chars)
                       && chars.ValueKind == JsonValueKind.Array
            ? chars.EnumerateArray().Select(c => c.GetString()).OfType<string>().ToList()
            : [];

        return new ServerCapabilities
        {
            TextSync = sync,
            SendsSaveText = saveText,
            HasCompletion = IsEnabled(caps, "completionProvider"),
            CompletionTriggerCharacters = triggers,
            HasHover = IsEnabled(caps, "hoverProvider"),
            HasDefinition = IsEnabled(caps, "definitionProvider"),
            HasReferences = IsEnabled(caps, "referencesProvider"),
            HasRename = IsEnabled(caps, "renameProvider"),
            HasFormatting = IsEnabled(caps, "documentFormattingProvider"),
            HasCodeActions = IsEnabled(caps, "codeActionProvider"),
            HasSignatureHelp = IsEnabled(caps, "signatureHelpProvider"),
            SignatureTriggerCharacters = Strings(caps, "signatureHelpProvider", "triggerCharacters").Concat(Strings(caps, "signatureHelpProvider", "retriggerCharacters")).Distinct().ToList(),
            HasWorkspaceSymbols = IsEnabled(caps, "workspaceSymbolProvider"),
            HasDocumentSymbols = IsEnabled(caps, "documentSymbolProvider"),
            HasInlayHints = IsEnabled(caps, "inlayHintProvider"),
        };
    }

    public static IReadOnlyList<TextReplacement> ReadTextEdits(JsonElement result) =>
        result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray().Select(e => new TextReplacement(ReadRange(e.GetProperty("range")), String(e, "newText") ?? string.Empty)).ToList()
            : [];

    /// <summary>
    /// WorkspaceEdit: <c>documentChanges</c> (text document edits and file create / rename / delete
    /// operations, applied in order) or the older <c>changes</c> map (text edits only).
    /// </summary>
    public static WorkspaceEdit? ReadWorkspaceEdit(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var changes = new List<WorkspaceChange>();
        if (result.TryGetProperty("documentChanges", out var documentChanges) && documentChanges.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in documentChanges.EnumerateArray())
            {
                if (ReadWorkspaceChange(change) is { } read)
                {
                    changes.Add(read);
                }
            }
        }
        else if (result.TryGetProperty("changes", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var file in map.EnumerateObject())
            {
                if (DocumentUri.ToPath(file.Name) is { } path)
                {
                    changes.Add(new FileEdit(path, ReadTextEdits(file.Value)));
                }
            }
        }

        return new WorkspaceEdit(changes);
    }

    private static WorkspaceChange? ReadWorkspaceChange(JsonElement change)
    {
        if (change.TryGetProperty("textDocument", out var document) && change.TryGetProperty("edits", out var edits))
        {
            return DocumentUri.ToPath(String(document, "uri")) is { } path ? new FileEdit(path, ReadTextEdits(edits)) : null;
        }

        var options = change.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
        bool Option(string name) => options.ValueKind == JsonValueKind.Object && Bool(options, name);
        return String(change, "kind") switch
        {
            "create" when DocumentUri.ToPath(String(change, "uri")) is { } path => new CreateFile(path, Option("overwrite"), Option("ignoreIfExists")),
            "rename" when DocumentUri.ToPath(String(change, "oldUri")) is { } from && DocumentUri.ToPath(String(change, "newUri")) is { } to =>
                new RenameFile(from, to, Option("overwrite"), Option("ignoreIfExists")),
            "delete" when DocumentUri.ToPath(String(change, "uri")) is { } path => new DeleteFile(path, Option("recursive"), Option("ignoreIfNotExists")),
            _ => null,
        };
    }

    /// <summary>SymbolInformation[] | WorkspaceSymbol[] (whose location may have no range yet).</summary>
    public static IReadOnlyList<SymbolInformation> ReadWorkspaceSymbols(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var symbols = new List<SymbolInformation>();
        foreach (var item in result.EnumerateArray())
        {
            if (!item.TryGetProperty("location", out var location) || DocumentUri.ToPath(String(location, "uri")) is not { } path)
            {
                continue;
            }

            var range = location.TryGetProperty("range", out var r) && r.ValueKind == JsonValueKind.Object ? ReadRange(r) : default;
            symbols.Add(new SymbolInformation(String(item, "name") ?? string.Empty, Kind(item), String(item, "containerName"), new Location(path, range)));
        }

        return symbols;
    }

    /// <summary>DocumentSymbol[] (a tree) | SymbolInformation[] (flat, turned into symbols without children).</summary>
    public static IReadOnlyList<DocumentSymbol> ReadDocumentSymbols(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return result.EnumerateArray().Select(ReadDocumentSymbol).OfType<DocumentSymbol>().ToList();

        static DocumentSymbol? ReadDocumentSymbol(JsonElement item)
        {
            if (item.TryGetProperty("location", out var location))
            {
                var flat = ReadRange(location.GetProperty("range"));
                return new DocumentSymbol(String(item, "name") ?? string.Empty, String(item, "containerName"), Kind(item), flat, flat, []);
            }

            if (!item.TryGetProperty("range", out var range))
            {
                return null;
            }

            var selection = item.TryGetProperty("selectionRange", out var s) ? ReadRange(s) : ReadRange(range);
            var children = item.TryGetProperty("children", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(ReadDocumentSymbol).OfType<DocumentSymbol>().ToList()
                : [];
            return new DocumentSymbol(String(item, "name") ?? string.Empty, String(item, "detail"), Kind(item), ReadRange(range), selection, children);
        }
    }

    /// <summary>InlayHint[]; a label made of parts is joined into one string.</summary>
    public static IReadOnlyList<InlayHint> ReadInlayHints(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var hints = new List<InlayHint>();
        foreach (var item in result.EnumerateArray())
        {
            if (!item.TryGetProperty("position", out var position) || !item.TryGetProperty("label", out var label))
            {
                continue;
            }

            var text = label.ValueKind == JsonValueKind.String
                ? label.GetString() ?? string.Empty
                : label.ValueKind == JsonValueKind.Array ? string.Concat(label.EnumerateArray().Select(part => String(part, "value"))) : string.Empty;
            if (text.Length > 0)
            {
                hints.Add(new InlayHint(ReadPosition(position), text, (InlayHintKind)Int(item, "kind"), Bool(item, "paddingLeft"), Bool(item, "paddingRight")));
            }
        }

        return hints;
    }

    private static SymbolKind Kind(JsonElement item) => Int(item, "kind") is >= 1 and <= 26 and var kind ? (SymbolKind)kind : SymbolKind.Variable;

    public static SignatureHelp? ReadSignatureHelp(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("signatures", out var array) || array.GetArrayLength() == 0)
        {
            return null;
        }

        var signatures = new List<SignatureInfo>();
        foreach (var signature in array.EnumerateArray())
        {
            var label = String(signature, "label") ?? string.Empty;
            var parameters = new List<(int, int)>();
            var searchFrom = 0;
            if (signature.TryGetProperty("parameters", out var parameterArray) && parameterArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var parameter in parameterArray.EnumerateArray())
                {
                    if (!parameter.TryGetProperty("label", out var parameterLabel))
                    {
                        continue;
                    }

                    if (parameterLabel.ValueKind == JsonValueKind.Array && parameterLabel.GetArrayLength() == 2)
                    {
                        parameters.Add((parameterLabel[0].GetInt32(), parameterLabel[1].GetInt32()));
                    }
                    else if (parameterLabel.GetString() is { Length: > 0 } text)
                    {
                        // A text label: find it in the signature, after the previous parameter.
                        var at = label.IndexOf(text, searchFrom, StringComparison.Ordinal);
                        parameters.Add(at < 0 ? (0, 0) : (at, at + text.Length));
                        searchFrom = at < 0 ? searchFrom : at + text.Length;
                    }
                }
            }

            var documentation = signature.TryGetProperty("documentation", out var doc) ? ReadMarkup(doc).Text : null;
            int? active = signature.TryGetProperty("activeParameter", out var ap) && ap.ValueKind == JsonValueKind.Number ? ap.GetInt32() : null;
            signatures.Add(new SignatureInfo(label, string.IsNullOrWhiteSpace(documentation) ? null : documentation, parameters, active));
        }

        return new SignatureHelp(signatures, Int(result, "activeSignature"), Int(result, "activeParameter"));
    }

    /// <summary>(Command | CodeAction)[]; disabled actions are left out.</summary>
    public static IReadOnlyList<CodeAction> ReadCodeActions(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var actions = new List<CodeAction>();
        foreach (var item in result.EnumerateArray())
        {
            if (item.TryGetProperty("disabled", out _))
            {
                continue;
            }

            var title = String(item, "title") ?? string.Empty;
            if (item.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String)
            {
                actions.Add(new CodeAction(title, null, false, null, ReadCommand(item))); // a bare Command
                continue;
            }

            actions.Add(new CodeAction(
                title,
                String(item, "kind"),
                Bool(item, "isPreferred"),
                item.TryGetProperty("edit", out var edit) ? ReadWorkspaceEdit(edit) : null,
                item.TryGetProperty("command", out var nested) && nested.ValueKind == JsonValueKind.Object ? ReadCommand(nested) : null));
        }

        return actions;
    }

    public static JsonObject CommandJson(LspCommand command)
    {
        var json = new JsonObject { ["command"] = command.Name };
        if (command.Arguments is { } arguments)
        {
            json["arguments"] = JsonNode.Parse(arguments.GetRawText());
        }

        return json;
    }

    private static LspCommand ReadCommand(JsonElement element) => new(
        String(element, "title") ?? string.Empty,
        String(element, "command") ?? string.Empty,
        element.TryGetProperty("arguments", out var arguments) ? arguments.Clone() : null);

    private static IEnumerable<string> Strings(JsonElement element, string objectName, string arrayName) =>
        element.TryGetProperty(objectName, out var inner) && inner.ValueKind == JsonValueKind.Object
        && inner.TryGetProperty(arrayName, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(c => c.GetString()).OfType<string>()
            : [];

    private static Location? ReadLocation(JsonElement element)
    {
        if (element.TryGetProperty("targetUri", out var targetUri))
        {
            var range = element.TryGetProperty("targetSelectionRange", out var selection) ? selection : element.GetProperty("targetRange");
            return DocumentUri.ToPath(targetUri.GetString()) is { } target ? new Location(target, ReadRange(range)) : null;
        }

        return element.TryGetProperty("uri", out var uri) && DocumentUri.ToPath(uri.GetString()) is { } path
            ? new Location(path, ReadRange(element.GetProperty("range")))
            : null;
    }

    /// <summary>MarkupContent | MarkedString | MarkedString[].</summary>
    private static (string Text, bool IsMarkdown) ReadMarkup(JsonElement contents)
    {
        switch (contents.ValueKind)
        {
            case JsonValueKind.String:
                return (contents.GetString() ?? string.Empty, true);
            case JsonValueKind.Array:
                var parts = contents.EnumerateArray().Select(ReadMarkup).Where(p => p.Text.Length > 0).Select(p => p.Text);
                return (string.Join("\n\n---\n\n", parts), true);
            case JsonValueKind.Object when contents.TryGetProperty("kind", out var kind):
                return (String(contents, "value") ?? string.Empty, kind.GetString() == "markdown");
            case JsonValueKind.Object:
                // MarkedString { language, value } is a code block.
                var language = String(contents, "language") ?? string.Empty;
                return ($"```{language}\n{String(contents, "value")}\n```", true);
            default:
                return (string.Empty, false);
        }
    }

    private static IReadOnlyList<TextReplacement> ReadEdits(JsonElement element, string name) =>
        element.TryGetProperty(name, out var edits) && edits.ValueKind == JsonValueKind.Array
            ? edits.EnumerateArray().Select(e => new TextReplacement(ReadRange(e.GetProperty("range")), String(e, "newText") ?? string.Empty)).ToList()
            : [];

    private static string? LabelDetail(JsonElement element) =>
        element.TryGetProperty("labelDetails", out var details) && details.ValueKind == JsonValueKind.Object
            ? (String(details, "detail") + " " + String(details, "description")).Trim() is { Length: > 0 } text ? text : null
            : null;

    private static bool IsEnabled(JsonElement caps, string name) =>
        caps.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.Object;

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
