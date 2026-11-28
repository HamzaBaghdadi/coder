using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.App.Workbench.Agent;
using Coder.Presentation.Agent;
using Coder.Presentation.Editors;

namespace Coder.App.Workbench;

/// <summary>
/// A Markdown document drawn for reading (the preview of a .md file, an extension's README): a centered
/// column at a comfortable width, GitHub-like heading sizes, images (local files and web addresses, SVG too),
/// and links: web addresses open in the browser, links to files open them in Coder, "#section" scrolls there.
/// Lists, quotes, code and tables are drawn as in Coder Agent's chat.
/// </summary>
public sealed class MarkdownDocumentView : ScrollViewer
{
    private const double ColumnWidth = 860;

    private static readonly HttpClient Web = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, Task<byte[]?>> WebCache = [];

    private readonly StackPanel _column;
    private readonly Dictionary<string, Control> _anchors = new(StringComparer.OrdinalIgnoreCase);
    private string? _baseFolder;
    private string _shownText = string.Empty;

    public MarkdownDocumentView()
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        _column = new StackPanel
        {
            Spacing = Spacing.M,
            MaxWidth = ColumnWidth,
            Margin = new Thickness(Spacing.XXL, Spacing.XL, Spacing.XXL, Spacing.XXL),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Content = _column;
        AutomationProperties.SetName(this, "Markdown Preview");
    }

    /// <summary>A subclass gets no template of its own: without this it would not scroll at all.</summary>
    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    /// <summary>True (the preview): the column is centered. False (an extension's page): it lines up on the left under a header.</summary>
    public bool CenterColumn { get; set; } = true;

    /// <summary>The column is as wide as the view allows, up to <see cref="ColumnWidth"/>, whatever its text.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && Bounds.Width > 0)
        {
            _column.Width = Math.Max(200, Math.Min(ColumnWidth, Bounds.Width - 2 * Spacing.XXL));
            _column.HorizontalAlignment = CenterColumn ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        }
    }

    /// <summary>Opens a file a relative link points to (the workbench sets it); null leaves such links inert.</summary>
    public Func<string, Task>? OpenFile { get; set; }

    /// <summary>The shown document's text (tests).</summary>
    internal string Text => string.Join("\n", _column.Children.Select(PlainText));

    /// <summary>Shows <paramref name="markdown"/>; relative images and links resolve against <paramref name="baseFolder"/>.</summary>
    public void Show(string markdown, string? baseFolder)
    {
        if (markdown == _shownText && baseFolder == _baseFolder)
        {
            return;
        }

        _shownText = markdown;
        _baseFolder = baseFolder;
        _anchors.Clear();
        _column.Children.Clear();
        foreach (var block in MarkdownDocument.Parse(markdown))
        {
            _column.Children.Add(Block(block));
        }

        if (_column.Children.Count == 0)
        {
            _column.Children.Add(new TextBlock { Text = "This document is empty.", Classes = { "secondary" } });
        }
    }

    private Control Block(ChatBlock block)
    {
        if (block.Kind == ChatBlockKind.Heading)
        {
            return Heading(block);
        }

        if (block.Kind == ChatBlockKind.Paragraph && block.Spans.Any(s => s.Kind is InlineKind.Image or InlineKind.Link))
        {
            return RichParagraph(block.Spans);
        }

        return MarkdownView.Block(block);
    }

    /// <summary>h1 and h2 are large with a rule under them, like GitHub; each is an anchor for "#slug" links.</summary>
    private Control Heading(ChatBlock block)
    {
        var (size, rule) = block.Level switch
        {
            1 => (Typography.DisplaySize - 2, true),
            2 => (Typography.HeadingSize + 2, true),
            3 => (Typography.HeadingSize - 2, false),
            4 => (Typography.TitleSize, false),
            _ => (Typography.UiSize, false),
        };
        var text = MarkdownView.Paragraph(block.Spans, size, Typography.Strong);
        Control heading = rule
            ? new Border { BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, Spacing.S), Child = text }
            : text;
        heading.Margin = new Thickness(0, block.Level <= 2 ? Spacing.L : Spacing.S, 0, 0);
        _anchors.TryAdd(Slug(block.PlainText), heading);
        AutomationProperties.SetHeadingLevel(heading, block.Level);
        return heading;
    }

    /// <summary>A paragraph with images or links: a wrapping row of text runs, link buttons and pictures.</summary>
    private WrapPanel RichParagraph(IReadOnlyList<InlineSpan> spans)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        var run = new List<InlineSpan>();
        void FlushText()
        {
            if (run.Count > 0)
            {
                row.Children.Add(MarkdownView.Paragraph([.. run]));
                run.Clear();
            }
        }

        foreach (var span in spans)
        {
            switch (span.Kind)
            {
                case InlineKind.Image:
                    FlushText();
                    row.Children.Add(Picture(span));
                    break;
                case InlineKind.Link:
                    FlushText();
                    row.Children.Add(Link(span));
                    break;
                default:
                    run.Add(span);
                    break;
            }
        }

        FlushText();
        return row;
    }

    private TextBlock Link(InlineSpan span)
    {
        var link = new TextBlock
        {
            Text = span.Text,
            Foreground = Palette.Accent,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
            LineHeight = Typography.UiSize * 1.5,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        ToolTip.SetTip(link, span.Url);
        AutomationProperties.SetName(link, span.Text);
        link.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            await FollowAsync(span.Url ?? string.Empty);
        };
        return link;
    }

    /// <summary>Web links open in the browser, "#section" scrolls to the heading, other links open the file in Coder.</summary>
    private async Task FollowAsync(string url)
    {
        if (url.StartsWith('#'))
        {
            if (_anchors.TryGetValue(url[1..], out var target))
            {
                target.BringIntoView();
            }

            return;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto))
        {
            if (TopLevel.GetTopLevel(this) is { } top)
            {
                await top.Launcher.LaunchUriAsync(uri);
            }

            return;
        }

        if (Resolve(url.Split('#')[0]) is { } path && File.Exists(path) && OpenFile is not null)
        {
            await OpenFile(path);
        }
    }

    /// <summary>An image: shown when it loads (local files at once, web images when downloaded), its alt text until then.</summary>
    private Control Picture(InlineSpan span)
    {
        var host = new Border
        {
            Margin = new Thickness(0, 0, Spacing.XS, Spacing.XS),
            MaxWidth = ColumnWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = span.Text.Trim(), Classes = { "muted" }, FontSize = Typography.SmallSize },
        };
        AutomationProperties.SetName(host, span.Text.Trim().Length == 0 ? "Image" : span.Text.Trim());
        _ = LoadAsync(host, span.Url ?? string.Empty);
        return host;
    }

    private async Task LoadAsync(Border host, string source)
    {
        try
        {
            byte[]? bytes;
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                if (!WebCache.TryGetValue(source, out var download))
                {
                    WebCache[source] = download = DownloadAsync(uri);
                }

                bytes = await download;
            }
            else
            {
                bytes = Resolve(source) is { } path && File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
            }

            if (bytes is not null && Decode(bytes, source) is { } picture)
            {
                host.Child = picture;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // The alt text stays.
        }
    }

    private static async Task<byte[]?> DownloadAsync(Uri uri)
    {
        try
        {
            using var response = await Web.GetAsync(uri);
            return response.IsSuccessStatusCode && response.Content.Headers.ContentLength is null or < 20_000_000
                ? await response.Content.ReadAsByteArrayAsync()
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>A bitmap (PNG, JPEG, GIF, WebP…) or an SVG, shown at its own size up to the column's width.</summary>
    private static Control? Decode(byte[] bytes, string source)
    {
        if (DocumentImages.TryCreateSvg(bytes, source) is { } svg)
        {
            return svg;
        }

        using var stream = new MemoryStream(bytes);
        var bitmap = new Bitmap(stream);
        return new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            MaxWidth = Math.Min(bitmap.Size.Width, ColumnWidth),
            MaxHeight = Math.Min(bitmap.Size.Height, 1200),
        };
    }

    private string? Resolve(string relative)
    {
        if (relative.Length == 0)
        {
            return null;
        }

        var decoded = Uri.UnescapeDataString(relative);
        return Path.IsPathRooted(decoded) ? decoded : _baseFolder is null ? null : Path.GetFullPath(Path.Combine(_baseFolder, decoded));
    }

    /// <summary>GitHub's heading anchors: lower case, spaces to hyphens, punctuation dropped.</summary>
    internal static string Slug(string heading)
    {
        var slug = new StringBuilder();
        foreach (var c in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                slug.Append(c);
            }
            else if (c == ' ')
            {
                slug.Append('-');
            }
        }

        return slug.ToString();
    }

    private static string PlainText(Control control) => control switch
    {
        TextBlock text => text.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.OfType<Run>().Select(r => r.Text)) : text.Text ?? string.Empty,
        Panel panel => string.Join(" ", panel.Children.Select(PlainText)),
        Decorator { Child: { } child } => PlainText(child),
        ContentControl { Content: Control child } => PlainText(child),
        _ => string.Empty,
    };
}

/// <summary>How documents show SVG images (README badges are SVG); the SVG renderer plugs in here.</summary>
internal static class DocumentImages
{
    public static Control? TryCreateSvg(byte[] bytes, string source)
    {
        var looksSvg = source.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || source.Contains(".svg?", StringComparison.OrdinalIgnoreCase)
            || (bytes.Length > 0 && Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512)).Contains("<svg", StringComparison.OrdinalIgnoreCase));
        return looksSvg ? CreateSvg?.Invoke(Encoding.UTF8.GetString(bytes)) : null;
    }

    /// <summary>An SVG drawn at its own size (badges, diagrams), or null when it can't be read.</summary>
    public static Func<string, Control?>? CreateSvg { get; set; } = text =>
    {
        try
        {
            var document = Core.Svg.SvgDocument.Parse(text);
            var scale = Math.Min(1, 860 / Math.Max(1, document.Width)); // no wider than the column
            return new Controls.SvgView
            {
                Document = document,
                Width = document.Width * scale,
                Height = document.Height * scale,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
        }
        catch (Core.Svg.SvgException)
        {
            return null;
        }
    };
}
