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
    string Source);

/// <summary>An extension in Coder's extensions folder.</summary>
/// <param name="Themes">The color themes it contributes (their labels).</param>
public sealed record InstalledExtension(string Id, string DisplayName, string Publisher, string Description, string Version, string Folder, IReadOnlyList<string> Themes)
{
    /// <summary>The extension's icon file (from package.json), when it has one.</summary>
    public string? IconPath { get; init; }
}

/// <summary>Somewhere extensions can be found.</summary>
public interface IExtensionSource
{
    /// <summary>Shown on each result ("Open VSX").</summary>
    string Name { get; }

    /// <summary>Extensions Coder can use that match the query (popular ones for an empty query).</summary>
    /// <exception cref="ExtensionException">The source can't be reached or answered with something unreadable.</exception>
    Task<IReadOnlyList<ExtensionListing>> SearchAsync(string query, CancellationToken cancellationToken = default);
}

/// <summary>Searching, downloading or installing failed; the message says why, for the user.</summary>
public sealed class ExtensionException(string message, Exception? inner = null) : Exception(message, inner);
