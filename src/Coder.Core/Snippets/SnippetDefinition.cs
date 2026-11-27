namespace Coder.Core.Snippets;

/// <summary>One snippet from an extension's snippet file, in VS Code's format.</summary>
/// <param name="Name">Its key in the file ("For Loop"), shown next to the prefix.</param>
/// <param name="Prefixes">What is typed to offer it ("for", "fori").</param>
/// <param name="Body">The snippet text (<c>for (${1:i} = 0; ...) {\n\t$0\n}</c>), lines joined with \n.</param>
/// <param name="Languages">VS Code language ids it is for; empty for every language (a scope-less <c>.code-snippets</c> file).</param>
/// <param name="Source">The extension it came from, by display name.</param>
public sealed record SnippetDefinition(string Name, IReadOnlyList<string> Prefixes, string Body, string? Description, IReadOnlyList<string> Languages, string Source)
{
    public bool AppliesTo(IReadOnlyCollection<string> languageIds) =>
        Languages.Count == 0 || Languages.Any(l => languageIds.Contains(l, StringComparer.OrdinalIgnoreCase));
}
