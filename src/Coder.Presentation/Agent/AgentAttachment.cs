namespace Coder.Presentation.Agent;

public enum AttachmentKind
{
    File,
    Folder,
    Image,
}

/// <summary>
/// Something attached to the next message: a text file (read when sent, so it is current), a folder (its
/// listing) or an image (its bytes, for vision models). Shown as a removable chip above the input.
/// </summary>
public sealed record AgentAttachment(AttachmentKind Kind, string Name)
{
    /// <summary>The file or folder; null for an image pasted from the clipboard.</summary>
    public string? Path { get; init; }

    /// <summary>An image's bytes.</summary>
    public byte[]? Data { get; init; }

    /// <summary>An image's media type ("image/png").</summary>
    public string? MediaType { get; init; }

    /// <summary>Image types every vision API accepts, by file extension.</summary>
    public static string? ImageMediaType(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => null,
    };

    /// <summary>Images the app has to convert (to PNG) before they can be sent.</summary>
    public static bool IsConvertibleImage(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() is ".bmp" or ".tif" or ".tiff" or ".ico";
}
