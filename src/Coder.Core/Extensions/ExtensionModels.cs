namespace Coder.Core.Extensions;

/// <summary>An extension offered by a source (Open VSX, Coder's registry), before it is installed.</summary>
/// <param name="Id">"publisher.name", as VS Code and Open VSX write it.</param>
/// <param name="DownloadUrl">The package: a .vsix (a zip whose <c>extension/</c> folder holds package.json) or a zip with package.json at its root.</param>
/// <param name="Sha256">The package's SHA-256 (hex), or null when the source gives none.</param>
/// <param name="Sha256Url">Where the source publishes the SHA-256 (Open VSX), read before installing.</param>
public sealed record ExtensionListing(
    string Id,
    string DisplayName,
    string Publisher,
    string Description,
    string Version,
    long DownloadCount,
    bool Verified,
    string? IconUrl,
    string DownloadUrl,
    string? Sha256,
    string? Sha256Url,
    string Source)
{
    /// <summary>The package includes a program Coder runs (with the user's consent), not just data.</summary>
    public bool RunsProgram { get; init; }

    /// <summary>Where the extension's README (Markdown) is, when the source says.</summary>
    public string? ReadmeUrl { get; init; }
}

/// <summary>A command an extension declares in its manifest (<c>contributes.commands</c>); its program runs it.</summary>
/// <param name="Category">Shown before the title in the palette ("Claude Code"); the extension's name when the manifest gives none.</param>
/// <param name="KeyBinding">Gesture text such as "Ctrl+Escape", or null.</param>
public sealed record ExtensionCommand(string Id, string Category, string Title, string? KeyBinding);

/// <summary>
/// A view an extension adds to the secondary side bar (<c>coder.views</c>): a tab whose content is a
/// terminal its program opens there. Showing the tab before that runs <see cref="Command"/>.
/// </summary>
public sealed record ExtensionView(string Id, string Title, string? Command);

/// <summary>An extension in Coder's extensions folder.</summary>
/// <param name="Themes">The color themes it contributes (their labels).</param>
public sealed record InstalledExtension(string Id, string DisplayName, string Publisher, string Description, string Version, string Folder, IReadOnlyList<string> Themes)
{
    /// <summary>The extension's icon file (from package.json), when it has one.</summary>
    public string? IconPath { get; init; }

    /// <summary>Where it was installed from ("Open VSX", "Coder"); null for extensions installed before Coder recorded it.</summary>
    public string? Source { get; init; }

    /// <summary>The languages it has snippets for (<c>contributes.snippets</c>), each once; "*" for a file of scoped snippets.</summary>
    public IReadOnlyList<string> SnippetLanguages { get; init; } = [];

    /// <summary>True when the manifest declares a program (<c>coder.program</c>), for any platform.</summary>
    public bool DeclaresProgram { get; init; }

    /// <summary>The program to run on this machine (inside the extension's folder), or null when it has none for this platform.</summary>
    public string? ProgramPath { get; init; }

    /// <summary>The commands it adds to the palette (<c>contributes.commands</c>).</summary>
    public IReadOnlyList<ExtensionCommand> Commands { get; init; } = [];

    /// <summary>The views it adds to the secondary side bar (<c>coder.views</c>).</summary>
    public IReadOnlyList<ExtensionView> Views { get; init; } = [];

    /// <summary>The SHA-256 of the package it was installed from, when Coder recorded it; consent to run its program is tied to it.</summary>
    public string? PackageSha256 { get; init; }
}

/// <summary>Somewhere extensions can be found.</summary>
public interface IExtensionSource
{
    /// <summary>Shown on each result ("Open VSX").</summary>
    string Name { get; }

    /// <summary>Extensions Coder can use that match the query (popular ones for an empty query).</summary>
    /// <exception cref="ExtensionException">The source can't be reached or answered with something unreadable.</exception>
    Task<IReadOnlyList<ExtensionListing>> SearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>The extension with this id ("publisher.name"), latest version, or null when the source doesn't have it (Settings Sync).</summary>
    /// <exception cref="ExtensionException">The source can't be reached.</exception>
    async Task<ExtensionListing?> FindAsync(string id, CancellationToken cancellationToken = default) =>
        (await SearchAsync(id, cancellationToken)).FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The listing's README (Markdown), or null when the source has none or it can't be fetched.</summary>
    Task<string?> GetReadmeAsync(ExtensionListing listing, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}

/// <summary>Searching, downloading or installing failed; the message says why, for the user.</summary>
public sealed class ExtensionException(string message, Exception? inner = null) : Exception(message, inner);
