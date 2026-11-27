namespace Coder.Core.Snippets;

/// <summary>
/// VS Code's language ids for a file, which snippet files are declared for. Coder's ids are VS Code's,
/// but some Coder languages cover several of VS Code's: a <c>.tsx</c> file is TypeScript in Coder and
/// <c>typescriptreact</c> in VS Code, a <c>.scss</c> file is CSS here and <c>scss</c> there.
/// </summary>
public static class SnippetLanguages
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jsx"] = "javascriptreact",
        [".tsx"] = "typescriptreact",
        [".scss"] = "scss",
        [".less"] = "less",
        [".jsonc"] = "jsonc",
        [".ini"] = "ini",
        [".mdx"] = "mdx",
    };

    /// <param name="languageId">Coder's language id for the file ("typescript").</param>
    public static IReadOnlyCollection<string> For(string languageId, string? filePath)
    {
        var extension = filePath is null ? null : Path.GetExtension(filePath);
        return extension is not null && ByExtension.TryGetValue(extension, out var specific) && specific != languageId
            ? [specific]
            : [languageId];
    }
}
