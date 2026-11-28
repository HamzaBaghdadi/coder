namespace Coder.Core.Extensions;

/// <summary>Finds an extension's README: from its source (a web address or local file) or in its installed folder.</summary>
public static class ExtensionReadme
{
    /// <summary>READMEs larger than this are not shown (a broken or hostile source).</summary>
    private const int MaxLength = 2_000_000;

    private static readonly string[] FileNames = ["README.md", "readme.md", "Readme.md", "README.markdown", "README"];

    /// <summary>The Markdown at <paramref name="address"/> (http(s) or a file path), or null when it can't be read.</summary>
    public static async Task<string?> FetchAsync(HttpClient http, string? address, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        try
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxLength)
                {
                    return null;
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                return text.Length > MaxLength ? null : text;
            }

            return await ReadFileAsync(address, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= MaxLength ? await File.ReadAllTextAsync(path, cancellationToken) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The README in an installed extension's folder (VS Code packages keep it next to package.json).</summary>
    public static async Task<string?> ReadInstalledAsync(InstalledExtension extension, CancellationToken cancellationToken = default)
    {
        foreach (var folder in new[] { extension.Folder, Path.Combine(extension.Folder, "extension") })
        {
            foreach (var name in FileNames)
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path))
                {
                    return await ReadFileAsync(path, cancellationToken);
                }
            }
        }

        return null;
    }
}
