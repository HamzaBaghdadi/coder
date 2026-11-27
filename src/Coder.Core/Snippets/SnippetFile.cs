using System.Text.Json;

namespace Coder.Core.Snippets;

/// <summary>
/// Reads a VS Code snippet file (<c>snippets/csharp.json</c> or a <c>.code-snippets</c> file): an object of
/// named snippets, each with a <c>prefix</c> (a string or several), a <c>body</c> (a string or lines), an
/// optional <c>description</c> and, in <c>.code-snippets</c> files, a <c>scope</c> of language ids.
/// Comments and trailing commas are allowed, as in VS Code.
/// </summary>
public static class SnippetFile
{
    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <param name="language">The language the manifest declares the file for; null for a file whose snippets carry their own scope.</param>
    /// <exception cref="JsonException">The file isn't JSON.</exception>
    public static IReadOnlyList<SnippetDefinition> Parse(string json, string? language, string source)
    {
        using var document = JsonDocument.Parse(json, Options);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new List<SnippetDefinition>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var snippet = property.Value;
            if (snippet.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var prefixes = Strings(snippet, "prefix").Where(p => p.Length > 0).ToList();
            var body = Strings(snippet, "body");
            if (prefixes.Count == 0 || body.Count == 0)
            {
                continue; // without a prefix a snippet can't be typed; VS Code offers those only in a picker
            }

            var description = Strings(snippet, "description") is { Count: > 0 } lines ? string.Join("\n", lines) : null;
            IReadOnlyList<string> languages = language is not null
                ? [language]
                : snippet.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String
                    ? scope.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [];
            result.Add(new SnippetDefinition(property.Name, prefixes, string.Join("\n", body).Replace("\r\n", "\n", StringComparison.Ordinal), description, languages, source));
        }

        return result;
    }

    /// <summary>A property that is a string or an array of strings.</summary>
    private static List<string> Strings(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) ? []
        : value.ValueKind == JsonValueKind.String ? [value.GetString()!]
        : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
        : [];
}
