namespace Coder.Presentation.Editors;

/// <summary>What a preview tab shows.</summary>
public enum PreviewKind
{
    Markdown,
    Svg,
}

/// <summary>
/// A rendered view of a Markdown or SVG file (VS Code's "Open Preview"). While the file is open in an editor
/// tab the preview follows its unsaved text as you type; otherwise it shows the file on disk and refreshes
/// when the file is saved.
/// </summary>
public sealed class PreviewTabViewModel : TabViewModel
{
    private static readonly Dictionary<string, PreviewKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = PreviewKind.Markdown,
        [".markdown"] = PreviewKind.Markdown,
        [".mdx"] = PreviewKind.Markdown,
        [".svg"] = PreviewKind.Svg,
    };

    private string _sourcePath;
    private string _text;
    private EditorTabViewModel? _source;

    public PreviewTabViewModel(string sourcePath, PreviewKind kind, string text)
    {
        _sourcePath = sourcePath;
        Kind = kind;
        _text = text;
    }

    public PreviewKind Kind { get; }

    public override string Title => $"Preview {Path.GetFileName(_sourcePath)}";

    public override string Description => _sourcePath;

    /// <summary>The previewed file; the tab strip shows its icon.</summary>
    public override string? FilePath
    {
        get => _sourcePath;
        set
        {
            if (value is not null && SetProperty(ref _sourcePath, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Description));
            }
        }
    }

    /// <summary>The text being shown (the editor's unsaved text while the file is open).</summary>
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    /// <summary>The folder relative links and images in the document resolve against.</summary>
    public string? BaseFolder => Path.GetDirectoryName(_sourcePath);

    /// <summary>Whether <paramref name="path"/> can be previewed, and as what.</summary>
    public static PreviewKind? KindOf(string? path) =>
        path is not null && Kinds.TryGetValue(Path.GetExtension(path), out var kind) ? kind : null;

    /// <summary>Follows an open editor tab's text (or stops following, with null).</summary>
    public void Follow(EditorTabViewModel? source)
    {
        if (_source is not null)
        {
            _source.Document.Changed -= OnSourceChanged;
        }

        _source = source;
        if (_source is not null)
        {
            _source.Document.Changed += OnSourceChanged;
            Text = _source.Document.GetText();
        }
    }

    public override void Close() => Follow(null);

    private void OnSourceChanged(object? sender, Core.Text.TextChange e) => Text = _source!.Document.GetText();
}
