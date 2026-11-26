using Coder.Core.Text;

namespace Coder.Core.Lsp;

/// <summary>A range in a document, in LSP terms: zero-based lines and UTF-16 columns (as <see cref="TextPosition"/>).</summary>
public readonly record struct TextRange(TextPosition Start, TextPosition End);

public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

/// <summary>A problem a language server reported for a document.</summary>
/// <remarks><see cref="Raw"/> is the server's own JSON, sent back unchanged when asking for quick fixes.</remarks>
public sealed record Diagnostic(TextRange Range, DiagnosticSeverity Severity, string Message, string? Source, string? Code, System.Text.Json.JsonElement? Raw = null);

/// <summary>Where a symbol is: a file and a range in it.</summary>
public sealed record Location(string Path, TextRange Range);

/// <summary>Hover text (Markdown or plain) and the range it describes.</summary>
public sealed record HoverInfo(string Text, bool IsMarkdown, TextRange? Range);

/// <summary>A replacement: <see cref="Range"/> becomes <see cref="NewText"/>.</summary>
public sealed record TextReplacement(TextRange Range, string NewText);

/// <summary>A completion suggestion, reduced to what the editor uses.</summary>
public sealed record CompletionItem
{
    public required string Label { get; init; }

    public CompletionItemKind Kind { get; init; } = CompletionItemKind.Text;

    /// <summary>Type or signature shown next to the label.</summary>
    public string? Detail { get; init; }

    public string? SortText { get; init; }

    public string? FilterText { get; init; }

    /// <summary>The text to insert when there is no <see cref="TextEdit"/>; falls back to the label.</summary>
    public string? InsertText { get; init; }

    public bool IsSnippet { get; init; }

    public TextReplacement? TextEdit { get; init; }

    /// <summary>Edits elsewhere in the file, such as an added import.</summary>
    public IReadOnlyList<TextReplacement> AdditionalTextEdits { get; init; } = [];

    public bool Preselect { get; init; }

    public string FilterKey => FilterText ?? Label;

    public string SortKey => SortText ?? Label;
}

/// <summary>Completion results. When <see cref="IsIncomplete"/>, typing more should ask the server again.</summary>
public sealed record CompletionList(IReadOnlyList<CompletionItem> Items, bool IsIncomplete);

/// <summary>Completion item kinds, numbered as in the protocol.</summary>
public enum CompletionItemKind
{
    Text = 1,
    Method = 2,
    Function = 3,
    Constructor = 4,
    Field = 5,
    Variable = 6,
    Class = 7,
    Interface = 8,
    Module = 9,
    Property = 10,
    Unit = 11,
    Value = 12,
    Enum = 13,
    Keyword = 14,
    Snippet = 15,
    Color = 16,
    File = 17,
    Reference = 18,
    Folder = 19,
    EnumMember = 20,
    Constant = 21,
    Struct = 22,
    Event = 23,
    Operator = 24,
    TypeParameter = 25,
}

/// <summary>How a server wants document changes: not at all, the whole text, or just the edits.</summary>
public enum TextSyncKind
{
    None = 0,
    Full = 1,
    Incremental = 2,
}

/// <summary>The server features the client uses.</summary>
public sealed record ServerCapabilities
{
    public TextSyncKind TextSync { get; init; } = TextSyncKind.Full;

    public bool SendsSaveText { get; init; }

    public bool HasCompletion { get; init; }

    public IReadOnlyList<string> CompletionTriggerCharacters { get; init; } = [];

    public bool HasHover { get; init; }

    public bool HasDefinition { get; init; }

    public bool HasReferences { get; init; }

    public bool HasRename { get; init; }

    public bool HasFormatting { get; init; }

    public bool HasCodeActions { get; init; }

    public bool HasSignatureHelp { get; init; }

    public IReadOnlyList<string> SignatureTriggerCharacters { get; init; } = [];

    public bool HasWorkspaceSymbols { get; init; }

    public bool HasDocumentSymbols { get; init; }

    public bool HasInlayHints { get; init; }
}

/// <summary>One entry of a <c>didChange</c> notification. A null range means the whole document.</summary>
public sealed record ContentChange(TextRange? Range, string Text);

/// <summary>One step of a <see cref="WorkspaceEdit"/>: text edits to a file, or creating, renaming or deleting one.</summary>
public abstract record WorkspaceChange;

/// <summary>Edits to one file, in the coordinates of its text when this step runs.</summary>
public sealed record FileEdit(string Path, IReadOnlyList<TextReplacement> Edits) : WorkspaceChange;

/// <summary>Creates an empty file (a refactoring that moves code to a new file creates it, then edits it).</summary>
public sealed record CreateFile(string Path, bool Overwrite, bool IgnoreIfExists) : WorkspaceChange;

/// <summary>Renames or moves a file (renaming a class can rename its file).</summary>
public sealed record RenameFile(string OldPath, string NewPath, bool Overwrite, bool IgnoreIfExists) : WorkspaceChange;

/// <summary>Deletes a file or folder.</summary>
public sealed record DeleteFile(string Path, bool Recursive, bool IgnoreIfNotExists) : WorkspaceChange;

/// <summary>Changes across files (rename, quick fixes, refactorings), applied in order.</summary>
public sealed record WorkspaceEdit(IReadOnlyList<WorkspaceChange> Changes)
{
    /// <summary>The text edits, file by file.</summary>
    public IEnumerable<FileEdit> Files => Changes.OfType<FileEdit>();

    public int EditCount => Files.Sum(f => f.Edits.Count) + Changes.Count(c => c is not FileEdit);
}

/// <summary>What a symbol is, numbered as in the protocol.</summary>
public enum SymbolKind
{
    File = 1,
    Module = 2,
    Namespace = 3,
    Package = 4,
    Class = 5,
    Method = 6,
    Property = 7,
    Field = 8,
    Constructor = 9,
    Enum = 10,
    Interface = 11,
    Function = 12,
    Variable = 13,
    Constant = 14,
    String = 15,
    Number = 16,
    Boolean = 17,
    Array = 18,
    Object = 19,
    Key = 20,
    Null = 21,
    EnumMember = 22,
    Struct = 23,
    Event = 24,
    Operator = 25,
    TypeParameter = 26,
}

/// <summary>A symbol found by name across the workspace (Go to Symbol in Workspace).</summary>
/// <param name="Container">The type or namespace it is in, when the server says.</param>
public sealed record SymbolInformation(string Name, SymbolKind Kind, string? Container, Location Location);

/// <summary>A symbol in one document, with the symbols inside it (Go to Symbol in Editor, the outline).</summary>
/// <param name="Range">The whole declaration.</param>
/// <param name="SelectionRange">Its name, to select when going there.</param>
public sealed record DocumentSymbol(string Name, string? Detail, SymbolKind Kind, TextRange Range, TextRange SelectionRange, IReadOnlyList<DocumentSymbol> Children);

/// <summary>Whether an inlay hint shows a type ("x: int") or a parameter name ("count:").</summary>
public enum InlayHintKind
{
    Other = 0,
    Type = 1,
    Parameter = 2,
}

/// <summary>Faint text the editor shows inside a line without it being part of the document.</summary>
public sealed record InlayHint(TextPosition Position, string Label, InlayHintKind Kind, bool PaddingLeft, bool PaddingRight);

/// <summary>How a file changed on disk, for <c>workspace/didChangeWatchedFiles</c>.</summary>
public enum FileChangeType
{
    Created = 1,
    Changed = 2,
    Deleted = 3,
}

/// <summary>One overload shown while typing arguments; <see cref="Parameters"/> are ranges in <see cref="Label"/>.</summary>
public sealed record SignatureInfo(string Label, string? Documentation, IReadOnlyList<(int Start, int End)> Parameters, int? ActiveParameter);

public sealed record SignatureHelp(IReadOnlyList<SignatureInfo> Signatures, int ActiveSignature, int ActiveParameter);

/// <summary>A command the server runs on request (<c>workspace/executeCommand</c>).</summary>
public sealed record LspCommand(string Title, string Name, System.Text.Json.JsonElement? Arguments);

/// <summary>A quick fix or refactoring: an edit to apply, a command to run, or both.</summary>
public sealed record CodeAction(string Title, string? Kind, bool IsPreferred, WorkspaceEdit? Edit, LspCommand? Command);
