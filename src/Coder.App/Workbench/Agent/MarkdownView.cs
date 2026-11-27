using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Editor;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Tokens = Coder.App.Theme.Spacing;
using Coder.Core.Syntax;
using Coder.Presentation.Agent;

namespace Coder.App.Workbench.Agent;

/// <summary>
/// Draws a chat reply's Markdown blocks. While a reply streams it is drawn again often, so blocks that
/// did not change keep their controls (and their text selection); only the changed tail is rebuilt.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    private List<ChatBlock> _shown = [];

    public MarkdownView()
    {
        Spacing = Tokens.S;
    }

    public void Show(IReadOnlyList<ChatBlock> blocks)
    {
        var same = 0;
        while (same < blocks.Count && same < _shown.Count && SameBlock(blocks[same], _shown[same]))
        {
            same++;
        }

        while (Children.Count > same)
        {
            Children.RemoveAt(Children.Count - 1);
        }

        for (var i = same; i < blocks.Count; i++)
        {
            Children.Add(Block(blocks[i]));
        }

        _shown = [.. blocks];
    }

    /// <summary>The visible text, for tests and copying.</summary>
    public string Text => string.Join("\n", _shown.Select(b => b.PlainText));

    private static bool SameBlock(ChatBlock a, ChatBlock b) =>
        a.Kind == b.Kind && a.Level == b.Level && a.Marker == b.Marker && a.IsOpen == b.IsOpen && a.Language == b.Language
        && a.Text == b.Text && a.PlainText == b.PlainText && a.Rows.Count == b.Rows.Count
        && a.Spans.Select(s => s.Kind).SequenceEqual(b.Spans.Select(s => s.Kind));

    public static Control Block(ChatBlock block) => block.Kind switch
    {
        ChatBlockKind.Heading => Paragraph(block.Spans, block.Level switch { 1 or 2 => Typography.TitleSize, _ => Typography.UiSize }, Typography.Strong),
        ChatBlockKind.ListItem => new DockPanel
        {
            Margin = new Thickness(Tokens.S + block.Level * Tokens.L, 0, 0, 0),
            Children =
            {
                new TextBlock { Text = block.Marker, Foreground = Palette.TextSecondary, MinWidth = 16, Margin = new Thickness(0, 0, Tokens.XS, 0) }.Dock(Dock.Left),
                Paragraph(block.Spans),
            },
        },
        ChatBlockKind.Quote => new Border
        {
            BorderBrush = Palette.BorderStrong,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(Tokens.M, Tokens.XXS),
            Child = Paragraph(block.Spans, foreground: Palette.TextSecondary),
        },
        ChatBlockKind.Code => CodeBlock(block),
        ChatBlockKind.Table => Table(block),
        ChatBlockKind.Rule => new Border { Height = 1, Background = Palette.Border, Margin = new Thickness(0, Tokens.XS) },
        _ => Paragraph(block.Spans),
    };

    public static SelectableTextBlock Paragraph(IReadOnlyList<InlineSpan> spans, double fontSize = Typography.UiSize, FontWeight weight = FontWeight.Normal, IBrush? foreground = null)
    {
        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            FontWeight = weight,
            LineHeight = fontSize * 1.5,
            Foreground = foreground ?? Palette.TextPrimary,
        };
        foreach (var span in spans)
        {
            text.Inlines!.Add(Inline(span));
        }

        return text;
    }

    private static Inline Inline(InlineSpan span)
    {
        switch (span.Kind)
        {
            case InlineKind.Bold:
                return new Run(span.Text) { FontWeight = FontWeight.SemiBold };
            case InlineKind.Italic:
                return new Run(span.Text) { FontStyle = FontStyle.Italic };
            case InlineKind.BoldItalic:
                return new Run(span.Text) { FontWeight = FontWeight.SemiBold, FontStyle = FontStyle.Italic };
            case InlineKind.Strike:
                return new Run(span.Text) { TextDecorations = TextDecorations.Strikethrough };
            case InlineKind.Code:
                return new Run(span.Text) { FontFamily = Typography.Code, FontSize = Typography.SmallSize, Foreground = Palette.AccentBright, Background = Palette.Pressed };
            case InlineKind.Link when Uri.TryCreate(span.Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp):
                var link = new TextBlock
                {
                    Text = span.Text,
                    Foreground = Palette.Accent,
                    TextDecorations = TextDecorations.Underline,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                ToolTip.SetTip(link, uri.ToString());
                link.PointerPressed += (_, e) =>
                {
                    if (TopLevel.GetTopLevel(link) is { } top)
                    {
                        _ = top.Launcher.LaunchUriAsync(uri);
                    }

                    e.Handled = true;
                };
                return new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.TextBottom };
            case InlineKind.Link:
                return new Run(span.Text) { Foreground = Palette.Accent };
            default:
                return new Run(span.Text);
        }
    }

    /// <summary>A fenced code block: its language and a Copy button above the colored code, which scrolls sideways.</summary>
    private static Control CodeBlock(ChatBlock block)
    {
        var language = CodeText.FindLanguage(block.Language);
        var copy = new FlatButton(Icons.Copy, "Copy Code", 14) { Width = 22, Height = 22 };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(copy)?.Clipboard is { } clipboard)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, block.Text);
                ToolTip.SetTip(copy, "Copied");
            }
        };

        var code = CodeText.Create(block.Text, language ?? Languages.PlainText, Typography.SmallSize, TextWrapping.NoWrap);
        code.Margin = new Thickness(Tokens.M, Tokens.S);
        return new Border
        {
            Background = Palette.EditorBackground,
            BorderBrush = Palette.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.RadiusLarge,
            ClipToBounds = true,
            Child = new DockPanel
            {
                Children =
                {
                    new DockPanel
                    {
                        Margin = new Thickness(Tokens.M, Tokens.XS, Tokens.XS, 0),
                        Children =
                        {
                            copy.Dock(Dock.Right),
                            new TextBlock
                            {
                                Text = language?.DisplayName ?? block.Language ?? "code",
                                FontSize = Typography.CaptionSize,
                                Foreground = Palette.TextMuted,
                                VerticalAlignment = VerticalAlignment.Center,
                            },
                        },
                    }.Dock(Dock.Top),
                    new ScrollViewer
                    {
                        Classes = { "slim" },
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        Content = code,
                    },
                },
            },
        };
    }

    private static Control Table(ChatBlock block)
    {
        var columns = block.Rows.Max(r => r.Count);
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        for (var r = 0; r < block.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < block.Rows[r].Count; c++)
            {
                var cell = Paragraph(block.Rows[r][c], Typography.SmallSize, r == 0 ? FontWeight.SemiBold : FontWeight.Normal);
                cell.TextWrapping = TextWrapping.NoWrap;
                grid.Children.Add(new Border
                {
                    BorderBrush = Palette.Border,
                    BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                    Background = r == 0 ? Palette.Hover : null,
                    Padding = new Thickness(Tokens.S, Tokens.XXS),
                    Child = cell,
                }.Row(r).Column(c));
            }
        }

        AutomationProperties.SetName(grid, "Table");
        return new ScrollViewer
        {
            Classes = { "slim" },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }
}
