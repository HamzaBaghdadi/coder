using System.Text.Json;
using Coder.Core.Extensions;

namespace Coder.Core.Snippets;

/// <summary>
/// The snippets installed extensions contribute (<c>contributes.snippets</c> in package.json, as in VS
/// Code: <c>[{ "language": "csharp", "path": "./snippets/csharp.json" }]</c>). Read on first use and again
/// after an extension is installed or uninstalled. A file that can't be read is skipped.
/// </summary>
public sealed class SnippetCatalog
{
    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private readonly Func<IEnumerable<InstalledExtension>> _extensions;
    private IReadOnlyList<SnippetDefinition>? _all;

    public SnippetCatalog(ExtensionStore store)
        : this(store.Installed)
    {
        store.Changed += (_, _) => _all = null;
    }

    public SnippetCatalog(Func<IEnumerable<InstalledExtension>> extensions) => _extensions = extensions;

    public IReadOnlyList<SnippetDefinition> All => _all ??= Load();

    /// <summary>The snippets for a file whose VS Code language ids are these (see <see cref="SnippetLanguages"/>).</summary>
    public IReadOnlyList<SnippetDefinition> For(IReadOnlyCollection<string> languageIds) => All.Where(s => s.AppliesTo(languageIds)).ToList();

    private List<SnippetDefinition> Load()
    {
        var result = new List<SnippetDefinition>();
        foreach (var extension in _extensions())
        {
            result.AddRange(Read(extension));
        }

        return result;
    }

    /// <summary>One extension's snippets; files outside its folder are ignored.</summary>
    internal static IEnumerable<SnippetDefinition> Read(InstalledExtension extension)
    {
        var result = new List<SnippetDefinition>();
        var root = Path.GetFullPath(extension.Folder) + Path.DirectorySeparatorChar;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(extension.Folder, "package.json")), Options);
            if (!manifest.RootElement.TryGetProperty("contributes", out var contributes) || !contributes.TryGetProperty("snippets", out var snippets) || snippets.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var entry in snippets.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("path", out var pathValue) || pathValue.GetString() is not { Length: > 0 } relative)
                {
                    continue;
                }

                var path = Path.GetFullPath(Path.Combine(extension.Folder, relative));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                {
                    continue;
                }

                var language = entry.TryGetProperty("language", out var languageValue) && languageValue.ValueKind == JsonValueKind.String ? languageValue.GetString() : null;
                try
                {
                    result.AddRange(SnippetFile.Parse(File.ReadAllText(path), language, extension.DisplayName));
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                {
                    // one broken file doesn't hide the extension's others
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // ExtensionStore only lists folders with a readable package.json; it may have changed since
        }

        return result;
    }
}
