namespace Coder.Core.Lsp;

/// <summary>Converts between file paths and the <c>file://</c> URIs the protocol uses.</summary>
public static class DocumentUri
{
    /// <summary>Paths compare without case on Windows, where the file system ignores it.</summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string FromPath(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>The local path of a <c>file://</c> URI, or null for other schemes.</summary>
    public static string? ToPath(string? uri)
    {
        if (uri is null || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
        {
            return null;
        }

        // Servers often escape the drive colon ("file:///d%3A/..."), which Uri then does not see as a
        // drive, so the path is unescaped and the drive recognized here.
        var path = Uri.UnescapeDataString(parsed.AbsolutePath);
        if (!string.IsNullOrEmpty(parsed.Host))
        {
            return Path.GetFullPath($"//{parsed.Host}{path}"); // UNC share
        }

        if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':')
        {
            path = path[1..];
        }

        return Path.GetFullPath(path);
    }
}
