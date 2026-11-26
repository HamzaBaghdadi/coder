using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coder.Core.Extensions;

/// <summary>
/// Open VSX (open-vsx.org), the open registry of VS Code extensions. Coder can use what is data in
/// them, not their JavaScript, so only color themes are offered for now.
/// </summary>
public sealed class OpenVsxSource(HttpClient http, string baseUrl = "https://open-vsx.org") : IExtensionSource
{
    private const int PageSize = 40;

    public string Name => "Open VSX";

    public async Task<IReadOnlyList<ExtensionListing>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var url = $"{baseUrl.TrimEnd('/')}/api/-/search?category=Themes&size={PageSize}&sortBy=downloadCount&sortOrder=desc"
                  + (query.Trim().Length > 0 ? "&query=" + Uri.EscapeDataString(query.Trim()) : string.Empty);
        OpenVsxSearch? result;
        try
        {
            result = await http.GetFromJsonAsync(url, ExtensionJson.Default.OpenVsxSearch, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ExtensionException("Open VSX couldn't be reached. Check your connection and try again.", exception);
        }

        return (result?.Extensions ?? [])
            .Where(e => !e.Deprecated && e.Files?.Download is not null && e.Namespace is not null && e.Name is not null)
            .Select(e => new ExtensionListing(
                $"{e.Namespace}.{e.Name}",
                e.DisplayName is { Length: > 0 } display ? display : e.Name!,
                e.Namespace!,
                e.Description ?? string.Empty,
                e.Version ?? "0.0.0",
                e.DownloadCount,
                e.Verified,
                e.Files!.Icon,
                e.Files.Download!,
                null,
                e.Files.Sha256,
                Name))
            .ToList();
    }
}

/// <summary>
/// Coder's own registry: one <c>index.json</c> (in a GitHub repository, where publishing is a pull
/// request, like Zed's), listing packages in the same layout as VS Code's. Its address is a setting.
/// </summary>
public sealed class CoderRegistrySource(HttpClient http, string indexUrl) : IExtensionSource
{
    private IReadOnlyList<ExtensionListing>? _all;

    public string Name => "Coder";

    public async Task<IReadOnlyList<ExtensionListing>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        _all ??= await LoadAsync(cancellationToken);
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _all.Where(e => words.All(word => e.DisplayName.Contains(word, StringComparison.OrdinalIgnoreCase)
                                                 || e.Id.Contains(word, StringComparison.OrdinalIgnoreCase)
                                                 || e.Description.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.DownloadCount)
            .ToList();
    }

    private async Task<IReadOnlyList<ExtensionListing>> LoadAsync(CancellationToken cancellationToken)
    {
        RegistryIndex? index;
        try
        {
            index = indexUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? await http.GetFromJsonAsync(indexUrl, ExtensionJson.Default.RegistryIndex, cancellationToken)
                : JsonSerializer.Deserialize(await File.ReadAllTextAsync(indexUrl, cancellationToken), ExtensionJson.Default.RegistryIndex);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or UnauthorizedAccessException
                                              or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ExtensionException($"The extension registry at {indexUrl} couldn't be read.", exception);
        }

        return (index?.Extensions ?? [])
            .Where(e => e.Publisher is { Length: > 0 } && e.Name is { Length: > 0 } && e.Download is { Length: > 0 })
            .Select(e => new ExtensionListing(
                $"{e.Publisher}.{e.Name}",
                e.DisplayName is { Length: > 0 } display ? display : e.Name!,
                e.Publisher!,
                e.Description ?? string.Empty,
                e.Version ?? "0.0.0",
                e.Downloads,
                e.Verified,
                e.Icon,
                Resolve(e.Download!),
                e.Sha256,
                null,
                Name))
            .ToList();
    }

    /// <summary>Package addresses may be relative to the index.</summary>
    private string Resolve(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out _) ? address
        : indexUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(new Uri(indexUrl), address).ToString()
        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(indexUrl)!, address));
}

internal sealed class OpenVsxSearch
{
    public List<OpenVsxExtension>? Extensions { get; set; }
}

internal sealed class OpenVsxExtension
{
    public string? Namespace { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    public long DownloadCount { get; set; }

    public bool Verified { get; set; }

    public bool Deprecated { get; set; }

    public OpenVsxFiles? Files { get; set; }
}

internal sealed class OpenVsxFiles
{
    public string? Download { get; set; }

    public string? Icon { get; set; }

    public string? Sha256 { get; set; }
}

/// <summary>Coder's registry index: <c>{ "extensions": [ { "publisher", "name", "version", "download", "sha256", ... } ] }</c>.</summary>
internal sealed class RegistryIndex
{
    public List<RegistryEntry>? Extensions { get; set; }
}

internal sealed class RegistryEntry
{
    public string? Publisher { get; set; }

    public string? Name { get; set; }

    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    public string? Version { get; set; }

    public string? Download { get; set; }

    public string? Sha256 { get; set; }

    public string? Icon { get; set; }

    public long Downloads { get; set; }

    public bool Verified { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(OpenVsxSearch))]
[JsonSerializable(typeof(RegistryIndex))]
internal sealed partial class ExtensionJson : JsonSerializerContext;
