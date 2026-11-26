using System.Collections.Frozen;

namespace Coder.Core.Syntax;

/// <summary>
/// Everything the generic <see cref="LineLexer"/> needs to color a language. Adding a language is a
/// data change in <see cref="Languages"/>, not new lexer code.
/// </summary>
public sealed class LanguageDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public IReadOnlyList<string> FileExtensions { get; init; } = [];

    /// <summary>Exact file names (e.g. <c>Dockerfile</c>) that select this language.</summary>
    public IReadOnlyList<string> FileNames { get; init; } = [];

    public string? LineComment { get; init; }

    public (string Open, string Close)? BlockComment { get; init; }

    /// <summary>Delimiter of strings that may span lines (e.g. <c>"""</c> in Python, backtick in JS).</summary>
    public string? MultiLineStringDelimiter { get; init; }

    public string StringQuotes { get; init; } = "\"'";

    public FrozenSet<string> Keywords { get; init; } = FrozenSet<string>.Empty;

    public FrozenSet<string> Types { get; init; } = FrozenSet<string>.Empty;

    public FrozenSet<string> Constants { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Treat <c>PascalCase</c> identifiers as type names (works well for C-family languages).</summary>
    public bool PascalCaseIsType { get; init; }

    /// <summary>Lines starting with <c>#</c> are directives: C preprocessor lines, Markdown headings.</summary>
    public bool HashLinesAreDirectives { get; init; }

    /// <summary>
    /// HTML/XML mode: names after <c>&lt;</c> are tags, names inside a tag are attributes, and quotes only
    /// start strings inside tags (so apostrophes in prose are not strings).
    /// </summary>
    public bool MarkupTags { get; init; }

    /// <summary>Characters, besides letters and digits, allowed inside identifiers.</summary>
    public string ExtraIdentifierChars { get; init; } = "_";

    /// <summary>Accent color hint (hex) used for this language's file icon.</summary>
    public string IconColor { get; init; } = "#9B9B9B";

    /// <summary>Brackets that the editor auto-closes and matches.</summary>
    public string Brackets { get; init; } = "()[]{}";

    public override string ToString() => DisplayName;

    internal static FrozenSet<string> Words(string spaceSeparated, bool ignoreCase = false) =>
        spaceSeparated.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToFrozenSet(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
