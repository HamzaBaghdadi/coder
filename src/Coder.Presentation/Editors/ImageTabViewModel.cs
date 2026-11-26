namespace Coder.Presentation.Editors;

/// <summary>An image file shown in a tab (decoded and drawn by the view).</summary>
public sealed class ImageTabViewModel(string path) : TabViewModel
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico",
    };

    private string? _filePath = path;

    public override string Title => Path.GetFileName(_filePath) ?? string.Empty;

    public override string Description => _filePath ?? string.Empty;

    public override string? FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    /// <summary>True for files shown as images rather than text (SVG stays text, as in VS Code).</summary>
    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path));
}
